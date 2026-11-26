using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>
/// Google Gemini as a Desktop Intelligence provider (#607), through the
/// Antigravity CLI (<c>agy</c>), Google's recommended terminal agent for
/// personal Google accounts and AI Pro and Ultra plans. Gemini CLI
/// (<c>gemini</c>) serves only enterprise and API-key accounts now, so it's
/// never picked up in its place.
/// </summary>
/// <remarks>
/// <para>
/// Each question is one headless run with stream-json in and out, so the
/// question goes on standard input. Antigravity's own tools can't be switched
/// off, so each run is fenced in three ways: session-only deny rules
/// (<c>--settings</c>, never saved) refuse commands, file writes, web pages
/// and the browser, and anything else that would need approval is refused
/// because a headless run can't ask; DashyNMS's tools are loaded up front
/// from a workspace configuration in a fresh folder of DashyNMS's own, so the
/// user's global configuration is never changed; and the stream guard stops
/// the answer on an unexpected tool or file read. Antigravity can only use
/// its own generated MCP output files for the current conversation.
/// </para>
/// <para>
/// Antigravity reads no header from the environment, so the bridge's token is
/// written into that folder's MCP configuration, which is deleted afterwards,
/// and the folder itself is refused to the agent's file tools. Antigravity
/// has no option for a system prompt, so the instructions open a chat's first
/// question. A conversation that's gone starts a new one without saying so;
/// that's caught by its ID, and the chat carries on with its visible turns.
/// </para>
/// </remarks>
public sealed partial class GeminiAgentProvider : IAgentProvider
{
    /// <summary>
    /// The MCP server name: its tools reach the agent as mcp_dashynms_desktop_….
    /// Not "dashynms", which a user's own DashyNMS MCP server may already use.
    /// </summary>
    internal const string BridgeServerName = "dashynms_desktop";

    internal const string BridgeToolPrefix = "mcp_" + BridgeServerName + "_";

    /// <summary>Antigravity's web search, allowed only when Settings allows it.</summary>
    internal const string WebSearchTool = "search_web";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Antigravity's tools that the deny rules, or a headless run's lack of
    /// approval, refuse. One that's attempted fails and the answer goes on;
    /// one that gets through stops it.
    /// </summary>
    private static readonly HashSet<string> Refused = new(StringComparer.Ordinal)
    {
        "run_command", "send_command_input", "command_status", "write_to_file", "replace_file_content", "multi_replace_file_content",
        "read_url_content", "open_browser_url",
        "read_browser_page", "run_workflow", "notebook_edit", "notebook_execution",
    };

    private readonly string _workingDirectory;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly HashSet<AgentProcess> _running = new();

    /// <summary>Counts retirements: a question that started before one never launches its process after it.</summary>
    private int _generation;
    private string? _executable;
    private bool _supportsOutputReadHook;
    private IReadOnlyList<AgentModel> _models = Array.Empty<AgentModel>();

    /// <param name="workingDirectory">A folder of DashyNMS's own; each question runs in a fresh, empty folder inside it, so Antigravity never sees a project.</param>
    public GeminiAgentProvider(string workingDirectory, ILogger logger)
    {
        _workingDirectory = workingDirectory;
        _logger = logger;
    }

    public string Id => AppSettings.IntelligenceProviderGemini;

    public string Name => "Gemini";

    public string Company => "Google";

    public AgentCapabilities Capabilities { get; } = new(WebSearch: true, Images: false, Usage: false);

    public async Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken)
    {
        var executable = AgentExecutables.Find(executablePath, "agy", Candidates());
        if (executable is null) return new AgentProviderStatus(AgentProviderState.NotInstalled);
        var versionLine = await AgentExecutables.VersionAsync(executable, cancellationToken).ConfigureAwait(false);
        if (versionLine is null)
        {
            return new AgentProviderStatus(AgentProviderState.NotInstalled, executable,
                Detail: "DashyNMS found Antigravity CLI but it didn't run. Reinstall it, or choose another executable below, then choose Check again.");
        }

        var version = Version().Match(versionLine) is { Success: true } m ? m.Value : versionLine;
        _supportsOutputReadHook = System.Version.TryParse(version, out var cliVersion) && cliVersion >= new System.Version(1, 5, 17);
        _executable = executable;

        // agy has no sign-in status command, but listing models needs a sign-in and says so without one.
        var output = await OutputAsync(executable, ["models"], cancellationToken).ConfigureAwait(false);
        var status = Status(executable, version, output, AccountHint());
        if (status.State == AgentProviderState.Ready)
        {
            var models = Models(output);
            lock (_sync) _models = models;
        }

        return status;
    }

    /// <summary>
    /// Where Antigravity's sign-in stands, from <c>agy models</c>. It reports
    /// no account, so the key is a digest of the Google Cloud project it chose
    /// for the account - different for each - and never shown.
    /// </summary>
    internal static AgentProviderStatus Status(string executable, string version, string modelsOutput, string? accountHint)
    {
        if (modelsOutput.Contains("sign in", StringComparison.OrdinalIgnoreCase) || modelsOutput.Contains("authentication required", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderStatus(AgentProviderState.SignedOut, executable, version);
        }

        if (Models(modelsOutput).Count == 0)
        {
            return new AgentProviderStatus(AgentProviderState.Unavailable, executable, version,
                Detail: $"Antigravity CLI {version} didn't list any models for this account. Update it, or check your Google AI plan, then choose Check again.");
        }

        return new AgentProviderStatus(AgentProviderState.Ready, executable, version,
            CodexAgentProvider.Digest("gemini", accountHint ?? "google"), "Signed in with your Google account");
    }

    /// <summary>The models <c>agy models</c> lists - an id, a tab, then the name - each with its reasoning level in the name.</summary>
    internal static IReadOnlyList<AgentModel> Models(string output)
    {
        var models = new List<AgentModel>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split('\t', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !ModelId().IsMatch(parts[0]) || parts[1].Length == 0) continue;
            models.Add(new AgentModel(parts[0], parts[1], Array.Empty<string>()));
        }

        return models;
    }

    public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        lock (_sync) return Task.FromResult(_models);
    }

    /// <summary>Antigravity reports tokens per answer, not plan limits, so there's no usage to show.</summary>
    public Task<AgentUsage?> ReadUsageAsync(CancellationToken cancellationToken) => Task.FromResult<AgentUsage?>(null);

    public async Task<AgentTurnResult> AskAsync(AgentTurn turn, AgentToolSession tools, IIntelligenceListener listener, CancellationToken cancellationToken)
    {
        int generation;
        lock (_sync) generation = _generation;
        var executable = _executable ?? throw new AgentProviderException("Gemini isn't set up yet. Check it in Settings › Providers.",
            IntelligenceFailure.Tools, AgentProviderState.NotInstalled);
        var workspace = Path.Combine(_workingDirectory, Guid.NewGuid().ToString("N"));
        var settings = Path.Combine(Path.GetTempPath(), "dashynms-gemini-" + Guid.NewGuid().ToString("N") + ".json");
        var stream = new StreamState(turn.Session) { WebSearch = turn.WebSearch };
        // Earlier CLIs have not been verified to enforce PreToolUse in headless mode.
        var readHook = _supportsOutputReadHook ? GeminiOutputGuard.CreateHook(Environment.ProcessPath) : null;
        var fileReadDenials = readHook is null ? ["read_file(*)"] : UserFileReadDenials();
        try
        {
            PrivateFiles.CreateFolder(workspace);
            PrivateFiles.CreateFolder(Path.Combine(workspace, ".agents"));
            await PrivateFiles.WriteAsync(Path.Combine(workspace, ".agents", "mcp_config.json"), McpConfiguration(tools.Endpoint, tools.Token).ToJsonString(), cancellationToken).ConfigureAwait(false);
            if (readHook is not null)
            {
                await PrivateFiles.WriteAsync(Path.Combine(workspace, ".agents", "hooks.json"), readHook.ToJsonString(), cancellationToken).ConfigureAwait(false);
            }
            await PrivateFiles.WriteAsync(settings, SessionSettings(workspace, UserServers(), fileReadDenials).ToJsonString(), cancellationToken).ConfigureAwait(false);
            var process = AgentProcess.Start(executable, Arguments(turn, settings), workspace);
            bool retired;
            lock (_sync)
            {
                retired = generation != _generation;
                if (!retired) _running.Add(process);
            }

            if (retired)
            {
                // Retired while this question was getting ready: nothing of it runs.
                await process.DisposeAsync().ConfigureAwait(false);
                throw new OperationCanceledException();
            }

            try
            {
                return await RunAsync(process, turn, stream, listener, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync) _running.Remove(process);
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(workspace);
            TryDeleteFile(settings);

            // A temporary chat isn't kept: Antigravity saves every conversation, its subagents' too.
            if (turn.Temporary) foreach (var conversation in stream.Conversations) ForgetConversation(conversation);
        }
    }

    /// <summary>The command line: documented options and plain values only - the question goes on standard input.</summary>
    internal static List<string> Arguments(AgentTurn turn, string settingsFile)
    {
        var args = new List<string>
        {
            "--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--disable-slash-commands",
            "--settings", settingsFile, "--model", turn.Model,
        };
        if (!turn.Temporary && turn.Session is { } session) args.AddRange(["--conversation", session]);
        return args;
    }

    /// <summary>
    /// The bridge as a Streamable HTTP server offering only the allowed tools,
    /// each loaded up front so the agent calls it directly rather than reading
    /// its description from a file first.
    /// </summary>
    internal static JsonObject McpConfiguration(Uri endpoint, string token)
    {
        var names = IntelligenceToolAllowlist.Names.Order(StringComparer.Ordinal).ToList();
        var eager = new JsonObject();
        foreach (var name in names) eager[name] = new JsonObject { ["eager"] = true };
        return new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [BridgeServerName] = new JsonObject
                {
                    ["serverUrl"] = endpoint.AbsoluteUri,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
                    ["enabledTools"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                    ["tools"] = eager,
                },
            },
        };
    }

    /// <summary>
    /// This run's permissions, never saved: DashyNMS's tools allowed; commands,
    /// writes, web pages, the browser, the user's own MCP servers and the
    /// workspace holding the token refused. The CLI's wildcard deny takes
    /// precedence over a narrower allow and would block generated output as
    /// well. A PreToolUse hook restricts Antigravity's implicit access, and
    /// headless review blocks external file access. The stream guard checks
    /// again that the tool and path match the active conversation.
    /// </summary>
    internal static JsonObject SessionSettings(string workspace, IEnumerable<string> userServers, IEnumerable<string> userFileReadDenials)
    {
        var deny = new List<string>
        {
            "command(*)", "write_file(*)", "read_url(*)", "execute_url(*)", "download_url(*)",
            // A relative rule follows agy's workspace root, including when an ancestor is a symlink.
            "read_file(./)",
            "read_file(" + workspace.Replace('\\', '/') + "/)",
        };
        deny.AddRange(userServers.Where(s => s != BridgeServerName).Select(s => "mcp(" + s + "/*)"));
        deny.AddRange(userFileReadDenials);
        return new JsonObject
        {
            ["permissions"] = new JsonObject
            {
                ["allow"] = new JsonArray("mcp(" + BridgeServerName + "/*)"),
                ["deny"] = new JsonArray(deny.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
            },
            // Newer versions default to Auto, which may read outside the workspace without asking.
            ["agentMode"] = "",
            ["allowNonWorkspaceAccess"] = false,
            ["toolPermission"] = "request-review",
            ["disableSlashCommands"] = true,
        };
    }

    /// <summary>The first question of a chat carries DashyNMS's instructions, since agy takes no system prompt; a resumed one already has them.</summary>
    internal static string Prompt(AgentTurn turn)
    {
        var question = (turn.Preamble ?? string.Empty) + turn.Question;
        return turn.Session is null || turn.Temporary
            ? "<instructions>\n" + turn.Instructions + "\n</instructions>\n\n" + question
            : question;
    }

    private async Task<AgentTurnResult> RunAsync(AgentProcess process, AgentTurn turn, StreamState stream, IIntelligenceListener listener, CancellationToken cancellationToken)
    {
        var message = new JsonObject { ["event"] = "user", ["message"] = new JsonObject { ["content"] = Prompt(turn) } };
        await process.WriteLineAsync(message.ToJsonString(), cancellationToken).ConfigureAwait(false);
        process.CloseInput();

        try
        {
            await foreach (var line in process.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                JsonObject? update;
                try
                {
                    update = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (update is null) continue;
                if (Read(update, stream, listener) is { } failure)
                {
                    if (stream.RefusedTool is { } tool) _logger.LogWarning("Intelligence: Gemini used {Tool}, which isn't allowed", tool);
                    _logger.LogWarning("Intelligence: a Gemini answer was stopped ({Reason})", failure.Failure);
                    throw failure;
                }

                if (stream.Finished) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.Finished) throw Failure(await process.StderrTailAsync().ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(stream.Answer)) listener.OnAnswerCompleted(stream.Answer);
        return new AgentTurnResult(turn.Temporary ? null : stream.Session, null);
    }

    /// <summary>One stream-json event: the conversation starting, a step, or the result.</summary>
    internal static AgentProviderException? Read(JsonObject update, StreamState stream, IIntelligenceListener listener)
    {
        switch (update["event"]?.ToString())
        {
            case "init":
                var conversation = update["conversation_id"]?.ToString();
                if (conversation is not null) stream.Conversations.Add(conversation);

                // A conversation that's gone is replaced by a new one with only a warning on the error output.
                if (stream.Session is { } asked && conversation is not null && conversation != asked)
                {
                    return new AgentProviderException("Gemini no longer has this conversation.", IntelligenceFailure.Model) { SessionLost = true };
                }

                stream.Session = conversation ?? stream.Session;
                return null;

            case "step_update" when update["step_update"] is JsonObject step:
                return Step(step, stream, listener);

            case "result" when update["result"] is JsonObject result:
                stream.Finished = true;
                if (result["status"]?.ToString() != "SUCCESS") return Failure(result["error"]?.ToString() ?? result["status"]?.ToString());
                stream.Answer = result["response"]?.ToString();
                return null;
        }

        return null;
    }

    private static AgentProviderException? Step(JsonObject step, StreamState stream, IIntelligenceListener listener)
    {
        var state = step["state"]?.ToString();
        var index = step["step_index"] is JsonValue i && i.TryGetValue<int>(out var number) ? number : -1;
        foreach (var subagent in step["subagent_info"]?["subagents"] as JsonArray ?? new JsonArray())
        {
            if (subagent?["conversation_id"]?.ToString() is { } id) stream.Conversations.Add(id);
        }

        switch (step["step_type"]?.ToString())
        {
            case "agent_response":
                if (step["text_delta"]?.ToString() is { Length: > 0 } text) listener.OnText(text);
                return null;

            case "tool" or "subagent":
                var toolInfo = step["tool_info"] as JsonObject;
                var tool = step["tool_name"]?.ToString() ?? toolInfo?["name"]?.ToString() ?? "an unnamed tool";
                var parameters = toolInfo?["parameters"] as JsonObject;
                if (IsBridgeTool(tool, parameters)) return null;
                if (stream.WebSearch && tool == WebSearchTool)
                {
                    // Shown as activity, as ChatGPT's searches are.
                    if (state == "ACTIVE" && stream.Searches.Add(index)) listener.OnToolStarted("web_search", "{}");
                    else if (state is "DONE" or "ERROR" && stream.Searches.Remove(index)) listener.OnToolFinished("web_search", state == "ERROR");
                    return null;
                }

                if (tool == "read_file")
                {
                    if (state is not ("ACTIVE" or "DONE")) return null;
                    if (step["conversation_id"]?.ToString() == stream.Session &&
                        GeminiOutputGuard.AllowsRead(stream.GeminiHome, stream.Session, parameters)) return null;
                }
                // Other refused tools fail on their own; only one that got through stops the answer.
                else if (Refused.Contains(tool) && state != "DONE") return null;
                stream.RefusedTool = tool;
                return new AgentProviderException(
                    "Gemini tried to use one of Antigravity's own tools, which DashyNMS doesn't allow in Intelligence chats, so the answer was stopped. Try asking again.",
                    IntelligenceFailure.Tools);
        }

        return null;
    }

    /// <summary>One of DashyNMS's tools, called directly or through Antigravity's general MCP tool.</summary>
    private static bool IsBridgeTool(string tool, JsonNode? parameters)
        => tool.StartsWith(BridgeToolPrefix, StringComparison.Ordinal) && IntelligenceToolAllowlist.Names.Contains(tool[BridgeToolPrefix.Length..])
           || tool == "call_mcp_tool" && parameters?["ServerName"]?.ToString() == BridgeServerName;

    /// <summary>Antigravity's error, worded for the person, with what to do next.</summary>
    internal static AgentProviderException Failure(string? text)
    {
        var detail = text ?? string.Empty;
        if (detail.Contains("authentication required", StringComparison.OrdinalIgnoreCase) || detail.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("log in", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderException(
                "Antigravity CLI isn't signed in any more. Run agy in a terminal to sign in, then choose Check again in Settings › Providers.",
                IntelligenceFailure.PlanUsage, AgentProviderState.SignedOut);
        }

        if (detail.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal) || detail.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderException(
                "Gemini has reached a usage limit for this Google account. Try again once it resets, or choose another model in the picker.",
                IntelligenceFailure.PlanUsage, usage: new AgentUsage(Array.Empty<AgentUsageWindow>(), DateTimeOffset.UtcNow, LimitReached: true));
        }

        if (detail.Contains("invalid model selection", StringComparison.OrdinalIgnoreCase) || detail.Contains("not recognized as a known model", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderException("This account can't use the chosen model. Choose another in the model picker.", IntelligenceFailure.Model);
        }

        return new AgentProviderException("Gemini couldn't answer. Try again.", IntelligenceFailure.Model);
    }

    public async Task RetireAsync()
    {
        List<AgentProcess> running;
        lock (_sync)
        {
            _generation++;
            running = _running.ToList();
        }
        foreach (var process in running) await process.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await RetireAsync().ConfigureAwait(false);

    private static async Task<string> OutputAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        await using var process = AgentProcess.Start(executable, arguments, Path.GetTempPath(), redirectInput: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusTimeout);
        try
        {
            await foreach (var line in process.ReadLinesAsync(timeout.Token).ConfigureAwait(false)) lines.Add(line);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        // The sign-in message goes to the error output.
        return string.Join("\n", lines) + "\n" + await process.StderrTailAsync().ConfigureAwait(false);
    }

    /// <summary>Antigravity's own folder.</summary>
    private static string Home() => GeminiOutputGuard.Home;

    /// <summary>The Google Cloud project Antigravity picked for the signed-in account, which tells accounts apart.</summary>
    private static string? AccountHint()
    {
        try
        {
            var path = Path.Combine(Home(), "antigravity-cli", "cache", "default_project_id.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } project ? project : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads only the CLI settings that could implicitly authorise an external file read.</summary>
    private static IEnumerable<string> UserFileReadDenials()
    {
        try
        {
            var path = Path.Combine(Home(), "antigravity-cli", "settings.json");
            return InheritedFileReadDenials(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ["read_file(*)"];
        }
    }

    /// <summary>Explicit session denies take precedence even if the CLI carries a global allow into the session.</summary>
    internal static IReadOnlyList<string> InheritedFileReadDenials(string settings)
    {
        try
        {
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var allow = JsonNode.Parse(settings, documentOptions: options)?["permissions"]?["allow"]?.AsArray();
            if (allow is null) return [];

            var deny = new List<string>();
            foreach (var value in allow)
            {
                var rule = value?.GetValue<string>()?.Trim() ?? "";
                if (rule.StartsWith("read_file(", StringComparison.Ordinal)) deny.Add(rule);
                // Antigravity grants read access implicitly for any path that permits writes.
                else if (rule.StartsWith("write_file(", StringComparison.Ordinal)) deny.Add("read_file(" + rule["write_file(".Length..]);
            }
            return deny;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A malformed configuration cannot establish that the CLI has no broader file grants.
            return ["read_file(*)"];
        }
    }

    /// <summary>The names of the user's own global MCP servers, each refused in DashyNMS's runs. Only the names are read.</summary>
    private static IEnumerable<string> UserServers()
    {
        try
        {
            var path = Path.Combine(Home(), "config", "mcp_config.json");
            if (!File.Exists(path)) return Array.Empty<string>();
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: options)?["mcpServers"] is JsonObject servers
                ? servers.Select(s => s.Key).Where(name => PlainName().IsMatch(name)).ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: the guard still stops any call to them.
            return Array.Empty<string>();
        }
    }

    /// <summary>Removes a temporary chat's conversation from Antigravity's store.</summary>
    private static void ForgetConversation(string conversation)
    {
        if (!Guid.TryParse(conversation, out _)) return;
        var store = Path.Combine(Home(), "antigravity-cli");
        TryDeleteFile(Path.Combine(store, "conversations", conversation + ".db"));
        TryDeleteFile(Path.Combine(store, "annotations", conversation + ".pbtxt"));
        TryDelete(Path.Combine(store, "brain", conversation));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: only this user can read it, and its token stops working when the agents are retired or DashyNMS closes.
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Where Google's installers put agy, beyond PATH: its own installer, and winget's package folder.</summary>
    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Programs", "Antigravity CLI", "agy.exe");
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "agy.exe");
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            string[] installed;
            try
            {
                installed = Directory.Exists(packages) ? Directory.GetDirectories(packages, "Google.AntigravityCLI_*") : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                installed = [];
            }

            foreach (var folder in installed) yield return Path.Combine(folder, "agy.exe");
        }
        else
        {
            yield return Path.Combine(home, ".local", "bin", "agy");
        }
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex Version();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$")]
    private static partial Regex ModelId();

    /// <summary>A server name safe to put in a permission rule.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex PlainName();

    /// <summary>What one question's stream has said so far.</summary>
    internal sealed class StreamState(string? session, string? geminiHome = null)
    {
        public string GeminiHome { get; } = geminiHome ?? Home();

        public string? Session { get; set; } = session;

        public string? Answer { get; set; }

        public bool Finished { get; set; }

        /// <summary>Web search was allowed for this question.</summary>
        public bool WebSearch { get; init; }

        /// <summary>Web searches started and not yet finished, by step.</summary>
        public HashSet<int> Searches { get; } = new();

        /// <summary>Every conversation the run used - its own and its subagents' - for forgetting a temporary chat.</summary>
        public HashSet<string> Conversations { get; } = new(StringComparer.Ordinal);

        /// <summary>The tool that stopped the answer, for the log: a tool name, never its arguments.</summary>
        public string? RefusedTool { get; set; }
    }
}

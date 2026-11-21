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
/// GitHub Copilot as a Desktop Intelligence provider (#606), through the
/// GitHub Copilot CLI and whatever account it's signed in with.
/// </summary>
/// <remarks>
/// <para>
/// Sign-in, models and quota come from the CLI's headless JSON-RPC mode - the
/// one GitHub's own SDK drives - so nothing about the account or its models is
/// guessed or hard-coded. Its account call also says whether the account has
/// Copilot at all and whether MCP servers are allowed for it, so an account
/// that can't use DashyNMS's tools is shown as unavailable, with why, instead
/// of failing on its first question.
/// </para>
/// <para>
/// Each question is one <c>copilot</c> run with JSON output, locked down: only
/// DashyNMS's tools are available (<c>--available-tools</c>), the built-in and
/// the user's own MCP servers are off, no custom instructions, memory, account
/// history sync or temporary folder, and the agent can't ask questions. The
/// CLI has no option for a system prompt, so the instructions open a chat's
/// first question. The CLI doesn't document reading a header from the
/// environment, so the bridge's token is written into the per-question MCP
/// configuration in a private temporary folder, deleted afterwards. Any tool
/// that isn't DashyNMS's stops the answer.
/// </para>
/// <para>
/// VS Code installs a <c>copilot</c> shim that only offers to install the CLI;
/// it comes first on PATH on many machines, so it's skipped in favour of the
/// CLI's own install.
/// </para>
/// </remarks>
public sealed partial class CopilotAgentProvider : IAgentProvider
{
    /// <summary>The MCP server name: its tools are mcp:dashynms-…, and no other server is loaded.</summary>
    internal const string BridgeServerName = AgentToolSession.ServerName;

    /// <summary>Copilot picks the model for each question: available on every plan, Free included.</summary>
    internal const string AutoModel = "auto";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    private readonly string _workingDirectory;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly HashSet<AgentProcess> _running = new();
    private string? _executable;
    private IReadOnlyList<AgentModel> _models = Array.Empty<AgentModel>();
    private AgentUsage? _usage;

    /// <param name="workingDirectory">An empty folder of DashyNMS's own for Copilot to run in, so it never sees a project.</param>
    public CopilotAgentProvider(string workingDirectory, ILogger logger)
    {
        _workingDirectory = workingDirectory;
        _logger = logger;
    }

    public string Id => AppSettings.IntelligenceProviderCopilot;

    public string Name => "GitHub Copilot";

    public string Company => "GitHub";

    public AgentCapabilities Capabilities { get; } = new(WebSearch: false, Images: true, Usage: true);

    public async Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken)
    {
        var executable = AgentExecutables.Find(executablePath, "copilot", Candidates().Where(c => !IsShim(c)))
            is { } found && (executablePath is not null || !IsShim(found)) ? found : FindOwnInstall();
        if (executable is null) return new AgentProviderStatus(AgentProviderState.NotInstalled);
        var versionLine = await AgentExecutables.VersionAsync(executable, cancellationToken).ConfigureAwait(false);
        if (versionLine is null || versionLine.Contains("install", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderStatus(AgentProviderState.NotInstalled, executable,
                Detail: "DashyNMS found a copilot command that isn't the GitHub Copilot CLI. Install the CLI, or choose its executable below, then choose Check again.");
        }

        var version = Version().Match(versionLine) is { Success: true } m ? m.Value : versionLine;
        _executable = executable;
        try
        {
            return await WithRuntimeAsync(executable, async (rpc, ct) =>
            {
                var auth = await rpc.RequestAsync("account.getCurrentAuth", new JsonObject(), ct).ConfigureAwait(false);
                var status = Status(executable, version, auth);
                if (status.State == AgentProviderState.SignedOut) return status;

                // Whether the account has Copilot at all: listing its models is refused when it doesn't.
                // The account call's own plan fields lag a sign-up, so they aren't relied on.
                try
                {
                    var models = Models(await rpc.RequestAsync("models.list", new JsonObject(), ct).ConfigureAwait(false));
                    lock (_sync) _models = models;
                    return status;
                }
                catch (JsonRpcException ex) when (IsRefusal(ex.Message))
                {
                    return status with { State = AgentProviderState.Unavailable, Detail = NoAccessDetail };
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonRpcException or AgentProviderException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Intelligence: the GitHub Copilot CLI didn't report its sign-in ({ErrorType})", ex.GetType().Name);
            return new AgentProviderStatus(AgentProviderState.Unavailable, executable, version,
                Detail: $"GitHub Copilot CLI {version} didn't report its sign-in. Update it to the latest version, then choose Check again.");
        }
    }

    /// <summary>
    /// Where the CLI's account stands, from <c>account.getCurrentAuth</c>. The
    /// account key is a digest of the host and login, so chats are kept per
    /// account without its name appearing in a folder name.
    /// </summary>
    internal static AgentProviderStatus Status(string executable, string version, JsonNode? current)
    {
        if (current?["authInfo"] is not JsonObject auth)
        {
            return new AgentProviderStatus(AgentProviderState.SignedOut, executable, version);
        }

        var host = auth["host"]?.ToString();
        var login = auth["login"]?.ToString();
        var type = auth["type"]?.ToString();
        var key = CodexAgentProvider.Digest("copilot", string.Join("|", host, login, type));
        var text = login is { Length: > 0 } ? $"Signed in as {login} (GitHub)" : "Signed in with a GitHub token";
        var ready = new AgentProviderStatus(AgentProviderState.Ready, executable, version, key, text);

        // MCP off is what the CLI enforces - it warns and drops DashyNMS's server - so it's
        // shown before the first question rather than after it. A field left out never blocks.
        if (auth["copilotUser"]?["is_mcp_enabled"] is JsonValue mcp && mcp.TryGetValue<bool>(out var mcpEnabled) && !mcpEnabled)
        {
            return ready with
            {
                State = AgentProviderState.Unavailable,
                Detail = "MCP servers are turned off for this Copilot account, and DashyNMS's network tools reach Copilot as one. Turn on \"MCP servers in Copilot\" at github.com/settings/copilot/features - or, if an organisation manages your Copilot, ask an administrator to allow them - then choose Check again.",
            };
        }

        return ready;
    }

    private const string NoAccessDetail =
        "This GitHub account can't use Copilot yet. Sign up for Copilot Free, or another plan, at github.com/settings/copilot - or, if an organisation manages your Copilot, ask an administrator to allow Copilot CLI - then choose Check again.";

    /// <summary>
    /// The account's models from <c>models.list</c>: those its policy hasn't
    /// turned off, with their own reasoning levels, after Auto.
    /// </summary>
    internal static IReadOnlyList<AgentModel> Models(JsonNode? list)
    {
        var models = new List<AgentModel> { new(AutoModel, "Auto", Array.Empty<string>(), Description: "Copilot chooses a model for each question.") };
        foreach (var model in list?["models"] as JsonArray ?? new JsonArray())
        {
            if (model?["id"]?.ToString() is not { Length: > 0 } id || id == AutoModel) continue;
            if (model["policy"]?["state"]?.ToString() == "disabled") continue;
            var efforts = (model["supportedReasoningEfforts"] as JsonArray ?? new JsonArray())
                .Select(e => e?.ToString()).OfType<string>().Where(e => e.Length > 0).ToList();
            var vendor = model["vendor"]?.ToString();
            models.Add(new AgentModel(id, model["name"]?.ToString() is { Length: > 0 } name ? name : id, efforts,
                model["defaultReasoningEffort"]?.ToString() is { } effort && efforts.Contains(effort) ? effort : null,
                vendor is { Length: > 0 } ? $"From {vendor}." : null));
        }

        return models;
    }

    /// <summary>The CLI's refusal for an account without Copilot, or one its organisation's policy blocks.</summary>
    internal static bool IsRefusal(string? text) => text is not null
        && (text.Contains("not authorized to use this Copilot feature", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Access denied by policy", StringComparison.OrdinalIgnoreCase)
            || text.Contains("403", StringComparison.Ordinal));

    public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        lock (_sync) return Task.FromResult(_models);
    }

    /// <summary>The account's monthly allowances from <c>account.getQuota</c>, or the last ones seen when the CLI can't be asked.</summary>
    public async Task<AgentUsage?> ReadUsageAsync(CancellationToken cancellationToken)
    {
        if (_executable is not { } executable) return null;
        try
        {
            var usage = await WithRuntimeAsync(executable,
                async (rpc, ct) => Usage(await rpc.RequestAsync("account.getQuota", new JsonObject(), ct).ConfigureAwait(false)),
                cancellationToken).ConfigureAwait(false);
            lock (_sync) return _usage = usage ?? _usage;
        }
        catch (Exception ex) when (ex is JsonRpcException or AgentProviderException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            lock (_sync) return _usage;
        }
    }

    /// <summary>Each limited quota as the share used and when it resets. Unlimited ones, and code completions, aren't shown.</summary>
    internal static AgentUsage? Usage(JsonNode? quota)
    {
        if (quota?["quotaSnapshots"] is not JsonObject snapshots) return null;
        var windows = new List<AgentUsageWindow>();
        var reached = false;
        foreach (var (key, value) in snapshots)
        {
            if (value is not JsonObject snapshot || key == "completions") continue;
            if (snapshot["isUnlimitedEntitlement"] is JsonValue u && u.TryGetValue<bool>(out var unlimited) && unlimited) continue;

            // A plan without the allowance - Free has no premium requests - lists it as zero, not as used up.
            if (snapshot["entitlementRequests"] is JsonValue e && e.TryGetValue<double>(out var entitled) && entitled <= 0) continue;
            double? used = snapshot["remainingPercentage"] is JsonValue r && r.TryGetValue<double>(out var remaining)
                ? Math.Round(Math.Clamp(100 - remaining, 0, 100), 1)
                : null;
            DateTimeOffset? resets = DateTimeOffset.TryParse(snapshot["resetDate"]?.ToString(), out var date) ? date : null;
            windows.Add(new AgentUsageWindow(QuotaLabel(key), used, resets));
            var carriesOn = Flag(snapshot, "usageAllowedWithExhaustedQuota") || Flag(snapshot, "overageAllowedWithExhaustedQuota");
            if (used >= 100 && !carriesOn) reached = true;
        }

        return windows.Count == 0 ? null : new AgentUsage(windows, DateTimeOffset.UtcNow, reached);
    }

    private static bool Flag(JsonObject snapshot, string name) => snapshot[name] is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;

    internal static string QuotaLabel(string key) => key switch
    {
        "premium_interactions" => "monthly premium requests",
        "chat" => "monthly chat",
        _ => "monthly " + key.Replace('_', ' '),
    };

    public async Task<AgentTurnResult> AskAsync(AgentTurn turn, AgentToolSession tools, IIntelligenceListener listener, CancellationToken cancellationToken)
    {
        var executable = _executable ?? throw new AgentProviderException("GitHub Copilot isn't set up yet. Check it in Settings › Intelligence.",
            IntelligenceFailure.Tools, AgentProviderState.NotInstalled);
        Directory.CreateDirectory(_workingDirectory);
        var files = Path.Combine(Path.GetTempPath(), "dashynms-copilot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(files);
        var mcp = Path.Combine(files, "mcp.json");
        string? session = null;
        try
        {
            await File.WriteAllTextAsync(mcp, McpConfiguration(tools.Endpoint, tools.Token).ToJsonString(), cancellationToken).ConfigureAwait(false);
            var process = AgentProcess.Start(executable, Arguments(turn, mcp, UserServers()), _workingDirectory);
            lock (_sync) _running.Add(process);
            try
            {
                var stream = new StreamState(turn.Session);
                try
                {
                    return await RunAsync(process, turn, stream, listener, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    session = stream.Session;
                }
            }
            finally
            {
                lock (_sync) _running.Remove(process);
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(files);

            // A temporary chat isn't kept: Copilot saves every session, so its folder goes once the answer is in.
            if (turn.Temporary && session is not null && Guid.TryParse(session, out _)) TryDelete(Path.Combine(Home(), "session-state", session));
        }
    }

    /// <summary>
    /// The command line: the CLI's documented options only, all plain values.
    /// The question goes on standard input and the MCP configuration in a file.
    /// The tool list comes last, since it takes every value up to the next option.
    /// </summary>
    internal static List<string> Arguments(AgentTurn turn, string mcpFile, IEnumerable<string> userServers)
    {
        var args = new List<string>
        {
            "--output-format", "json", "--stream", "on", "--no-color", "--no-auto-update",
            "--no-custom-instructions", "--no-ask-user", "--disallow-temp-dir", "--sync-session=off",
            "--disable-builtin-mcps", "--additional-mcp-config", "@" + mcpFile, "--allow-tool", BridgeServerName,
            "--model", turn.Model,
        };
        foreach (var server in userServers.Where(s => s != BridgeServerName)) args.AddRange(["--disable-mcp-server", server]);
        if (turn.Effort is { } effort) args.AddRange(["--reasoning-effort", effort]);
        if (!turn.Temporary && turn.Session is { } session) args.Add("--resume=" + session);
        args.Add("--available-tools");
        args.AddRange(IntelligenceToolAllowlist.Names.Order(StringComparer.Ordinal).Select(name => "mcp:" + BridgeServerName + "-" + name));
        return args;
    }

    /// <summary>The bridge as an HTTP MCP server offering only the allowed tools.</summary>
    internal static JsonObject McpConfiguration(Uri endpoint, string token) => new()
    {
        ["mcpServers"] = new JsonObject
        {
            [BridgeServerName] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = endpoint.AbsoluteUri,
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
                ["tools"] = new JsonArray(IntelligenceToolAllowlist.Names.Order(StringComparer.Ordinal).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            },
        },
    };

    /// <summary>
    /// The first question of a chat carries DashyNMS's instructions, since the
    /// CLI takes no system prompt; a resumed session already has them.
    /// </summary>
    internal static string Prompt(AgentTurn turn)
    {
        var question = (turn.Preamble ?? string.Empty) + turn.Question;
        return turn.Session is null || turn.Temporary
            ? "<instructions>\n" + turn.Instructions + "\n</instructions>\n\n" + question
            : question;
    }

    private async Task<AgentTurnResult> RunAsync(AgentProcess process, AgentTurn turn, StreamState stream, IIntelligenceListener listener, CancellationToken cancellationToken)
    {
        await process.WriteLineAsync(Prompt(turn), cancellationToken).ConfigureAwait(false);
        process.CloseInput();

        try
        {
            await foreach (var line in process.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message is null) continue;
                if (Read(message, stream, listener) is { } failure)
                {
                    if (stream.RefusedTool is { } tool) _logger.LogWarning("Intelligence: GitHub Copilot tried to use {Tool}, which isn't allowed", tool);
                    _logger.LogWarning("Intelligence: a GitHub Copilot answer was stopped ({Reason})", failure.Failure);
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
        if (!stream.Finished || stream.ExitCode is not (null or 0))
        {
            throw Failure(process.StderrTail, null, turn.Session is not null);
        }

        if (!string.IsNullOrWhiteSpace(stream.Answer)) listener.OnAnswerCompleted(stream.Answer);
        return new AgentTurnResult(turn.Temporary ? null : stream.Session, null);
    }

    /// <summary>One JSON event: the session, text, a tool starting, a warning or error, or the result.</summary>
    internal static AgentProviderException? Read(JsonObject message, StreamState stream, IIntelligenceListener listener)
    {
        var data = message["data"];
        switch (message["type"]?.ToString())
        {
            case "session.start":
                stream.Session = data?["sessionId"]?.ToString() ?? stream.Session;
                return null;

            case "session.warning":
                var warning = data?["message"]?.ToString() ?? string.Empty;
                var type = data?["warningType"]?.ToString();
                if (type == "policy" && warning.Contains("MCP", StringComparison.OrdinalIgnoreCase)
                    || type == "mcp" && warning.Contains("blocked", StringComparison.OrdinalIgnoreCase) && warning.Contains("'" + BridgeServerName + "'", StringComparison.Ordinal))
                {
                    return new AgentProviderException(
                        "Your Copilot policy blocks MCP servers, and DashyNMS's network tools reach Copilot as one, so it wasn't asked. If an organisation manages your Copilot, ask an administrator to allow them, then choose Check again in Settings › Intelligence.",
                        IntelligenceFailure.Tools, AgentProviderState.Unavailable);
                }

                return null;

            case "session.error":
                return Failure(data?["message"]?.ToString(), data?["errorType"]?.ToString(), resumed: false);

            case "assistant.message_delta" when ParentTool(data) is null:
                if (data?["deltaContent"]?.ToString() is { Length: > 0 } text) listener.OnText(text);
                return null;

            case "assistant.message" when ParentTool(data) is null:
                if (data?["content"]?.ToString() is { Length: > 0 } content) stream.Answer = content;
                return null;

            case "tool.execution_start":
                if (data?["mcpServerName"]?.ToString() != BridgeServerName)
                {
                    stream.RefusedTool = data?["toolName"]?.ToString() ?? "an unnamed tool";
                    return new AgentProviderException(
                        "GitHub Copilot tried to use one of its own tools, which DashyNMS doesn't allow in Intelligence chats, so the answer was stopped. Try asking again.",
                        IntelligenceFailure.Tools);
                }

                return null;

            case "result":
                stream.Finished = true;
                stream.Session = message["sessionId"]?.ToString() ?? stream.Session;
                stream.ExitCode = message["exitCode"] is JsonValue code && code.TryGetValue<int>(out var exit) ? exit : null;
                return null;
        }

        return null;
    }

    /// <summary>The tool call a message belongs to - a subagent's text - or null for the answer itself.</summary>
    private static JsonNode? ParentTool(JsonNode? data) => data?["parentToolCallId"];

    /// <summary>The CLI's error, worded for the person, with what to do next.</summary>
    internal static AgentProviderException Failure(string? text, string? errorType, bool resumed)
    {
        var detail = text ?? string.Empty;
        if (IsRefusal(detail))
        {
            return new AgentProviderException(
                "GitHub Copilot refused: this account, or its organisation's policy, doesn't allow Copilot CLI. Check the account in Settings › Intelligence.",
                IntelligenceFailure.PlanUsage, AgentProviderState.Unavailable);
        }

        if (errorType is "authentication" or "auth" || detail.Contains("copilot login", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || detail.Contains("authenticate", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderException(
                "GitHub Copilot isn't signed in any more. Run copilot login in a terminal, then choose Check again in Settings › Intelligence.",
                IntelligenceFailure.PlanUsage, AgentProviderState.SignedOut);
        }

        if (errorType is "quota" or "rate_limit" || detail.Contains("quota", StringComparison.OrdinalIgnoreCase) || detail.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("credits", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderException(
                "GitHub Copilot has reached a usage limit for this account. Check its usage in Settings › Intelligence, then try again once it resets, or choose Auto in the picker.",
                IntelligenceFailure.PlanUsage, usage: new AgentUsage(Array.Empty<AgentUsageWindow>(), DateTimeOffset.UtcNow, LimitReached: true));
        }

        if (resumed && detail.Contains("session", StringComparison.OrdinalIgnoreCase)
            && (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) || detail.Contains("no session", StringComparison.OrdinalIgnoreCase)))
        {
            return new AgentProviderException("GitHub Copilot no longer has this conversation.", IntelligenceFailure.Model) { SessionLost = true };
        }

        if (detail.Contains("model", StringComparison.OrdinalIgnoreCase)
            && (detail.Contains("not available", StringComparison.OrdinalIgnoreCase) || detail.Contains("not supported", StringComparison.OrdinalIgnoreCase)))
        {
            return new AgentProviderException("This account can't use the chosen model. Choose another in the model picker.", IntelligenceFailure.Model);
        }

        return new AgentProviderException("GitHub Copilot couldn't answer. Try again.", IntelligenceFailure.Model);
    }

    public async Task RetireAsync()
    {
        List<AgentProcess> running;
        lock (_sync) running = _running.ToList();
        foreach (var process in running) await process.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await RetireAsync().ConfigureAwait(false);

    /// <summary>
    /// Runs <paramref name="work"/> against the CLI's headless JSON-RPC mode,
    /// started for the purpose and stopped afterwards. It answers nothing it's
    /// asked by the CLI: account calls don't ask.
    /// </summary>
    private async Task<T> WithRuntimeAsync<T>(string executable, Func<JsonRpcConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_workingDirectory);
        var process = AgentProcess.Start(executable, ["--headless", "--no-auto-update", "--stdio"], _workingDirectory, framed: true);
        await using var rpc = new JsonRpcConnection(process, (_, _) => Task.CompletedTask,
            (_, _) => Task.FromException<JsonNode?>(new JsonRpcException(-32601, "Not supported")));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusTimeout);
        return await work(rpc, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>The CLI's own folder: <c>COPILOT_HOME</c>, or .copilot in the home folder.</summary>
    private static string Home() => Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    /// <summary>The names of the user's own MCP servers, each turned off for DashyNMS's runs. Only the names are read.</summary>
    private static IEnumerable<string> UserServers()
    {
        try
        {
            var path = Path.Combine(Home(), "mcp-config.json");
            if (!File.Exists(path)) return Array.Empty<string>();
            return JsonNode.Parse(File.ReadAllText(path))?["mcpServers"] is JsonObject servers
                ? servers.Select(s => s.Key).Where(name => PlainName().IsMatch(name)).ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: the tool list still keeps their tools out.
            return Array.Empty<string>();
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: the token in it stops working when the question ends.
        }
    }

    /// <summary>VS Code's stand-in, which only offers to install the real CLI.</summary>
    internal static bool IsShim(string path) => path.Contains("copilot-shim", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the CLI's own installers put it.</summary>
    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHubCopilotCLI", "copilot.exe");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "copilot.cmd");
        }
        else
        {
            yield return Path.Combine(home, ".local", "bin", "copilot");
            yield return "/opt/homebrew/bin/copilot";
            yield return "/usr/local/bin/copilot";
        }
    }

    private static string? FindOwnInstall() => Candidates().FirstOrDefault(c => !IsShim(c) && File.Exists(c));

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex Version();

    /// <summary>A server name safe to pass on a command line.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex PlainName();

    /// <summary>What one question's stream has said so far.</summary>
    internal sealed class StreamState(string? session)
    {
        public string? Session { get; set; } = session;

        public string? Answer { get; set; }

        public bool Finished { get; set; }

        public int? ExitCode { get; set; }

        /// <summary>The tool that stopped the answer, for the log: a tool name, never its arguments.</summary>
        public string? RefusedTool { get; set; }
    }
}

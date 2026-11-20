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
/// Claude Code as a Desktop Intelligence provider (#317), through its
/// supported headless mode: <c>claude -p</c> with stream-json output, one
/// process per question, using whatever login Claude Code already has.
/// </summary>
/// <remarks>
/// <para>
/// Each run is locked down: no built-in tools (<c>--tools ""</c>) - but for
/// WebSearch when Settings allows web search; never WebFetch, which could
/// carry network data to any address in a URL - only
/// DashyNMS's MCP server (<c>--strict-mcp-config</c>), none of the user's
/// settings files - so no hooks, plugins or permission rules of theirs -
/// no skills or slash commands, and nothing that would ask for permission is
/// allowed. DashyNMS's tools are pre-allowed by name; they reach the app
/// through <see cref="AgentToolBridge"/>, whose token is in the process's
/// environment and referenced, not written, in the MCP configuration file.
/// </para>
/// <para>
/// The instructions go in a file and the question on standard input, so
/// neither appears on a command line. A saved chat resumes its Claude Code
/// session; a temporary one isn't kept on disk at all, and each follow-up
/// carries the visible conversation instead. Stop ends the process.
/// </para>
/// </remarks>
public sealed partial class ClaudeCodeAgentProvider : IAgentProvider
{
    /// <summary>The MCP server name - its tools are mcp__dashynms__…, and no other server is loaded.</summary>
    internal const string BridgeServerName = AgentToolSession.ServerName;

    internal const string TokenVariable = "DASHYNMS_MCP_TOKEN";

    /// <summary>Claude Code's web search tool: results with sources, searched by Anthropic's provider.</summary>
    internal const string WebSearchTool = "WebSearch";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Claude Code's reasoning levels. Before <see cref="Models"/>: static fields start in order.</summary>
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The Claude models DashyNMS offers, by their full ids so each shows its
    /// version. Claude Code has no command to list models, so this follows
    /// Anthropic's current lineup and is updated with it; a model the account
    /// can't use says so when asked.
    /// </summary>
    internal static readonly IReadOnlyList<AgentModel> Models =
    [
        new("claude-opus-5-5", "Claude Opus 5.5", Efforts, Description: "The most capable for complex investigations."),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", Efforts, Description: "Fast and capable for everyday questions."),
        new("claude-haiku-5-5", "Claude Haiku 5.5", Efforts, Description: "The quickest, for simple lookups."),
        new("claude-fable-5-1", "Claude Fable 5.1", Efforts, Description: "The newest family, where the plan includes it or with usage credits."),
    ];

    private readonly string _workingDirectory;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly HashSet<AgentProcess> _running = new();
    private string? _executable;
    private AgentUsage? _usage;

    /// <param name="workingDirectory">An empty folder of DashyNMS's own for Claude Code to run in, so it never sees a project.</param>
    public ClaudeCodeAgentProvider(string workingDirectory, ILogger logger)
    {
        _workingDirectory = workingDirectory;
        _logger = logger;
    }

    public string Id => AppSettings.IntelligenceProviderClaudeCode;

    public string Name => "Claude Code";

    public string Company => "Anthropic";

    public AgentCapabilities Capabilities { get; } = new(WebSearch: true, Images: true, Usage: true);

    public async Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken)
    {
        var executable = AgentExecutables.Find(executablePath, "claude");
        if (executable is null) return new AgentProviderStatus(AgentProviderState.NotInstalled);
        var versionLine = await AgentExecutables.VersionAsync(executable, cancellationToken).ConfigureAwait(false);
        if (versionLine is null)
        {
            return new AgentProviderStatus(AgentProviderState.NotInstalled, executable,
                Detail: "DashyNMS found Claude Code but it didn't run. Reinstall it, or choose another executable below, then choose Check again.");
        }

        var version = Version().Match(versionLine) is { Success: true } m ? m.Value : versionLine;
        _executable = executable;
        var text = await OutputAsync(executable, ["auth", "status", "--json"], cancellationToken).ConfigureAwait(false);
        try
        {
            return Status(executable, version, JsonNode.Parse(text));
        }
        catch (JsonException)
        {
            return new AgentProviderStatus(AgentProviderState.Unavailable, executable, version,
                Detail: $"Claude Code {version} didn't report its sign-in. Update Claude Code to the latest version, then choose Check again.");
        }
    }

    /// <summary>
    /// Where Claude Code's login stands. The account key is a digest of the
    /// account and organisation - or the API route - so chats are kept per
    /// account without its name appearing in a folder name.
    /// </summary>
    internal static AgentProviderStatus Status(string executable, string version, JsonNode? status)
    {
        if (status?["loggedIn"]?.GetValue<bool>() != true)
        {
            return new AgentProviderStatus(AgentProviderState.SignedOut, executable, version);
        }

        var method = status["authMethod"]?.ToString();
        var provider = status["apiProvider"]?.ToString();
        var email = status["email"]?.ToString();
        var organisation = status["orgId"]?.ToString();
        var plan = status["subscriptionType"]?.ToString();
        var identity = string.Join("|", method, provider, email, organisation, status["configDirectory"]?.ToString());
        var text = method == "claude.ai" || email is not null
            ? "Signed in as " + (email ?? "a Claude account") + (plan is { Length: > 0 } ? $" (Claude {Capitalise(plan)})" : string.Empty)
            : provider is { Length: > 0 } and not "firstParty"
                ? $"Using {provider} - usage is billed to that account"
                : "Using an Anthropic API key - usage is billed to that API account";
        return new AgentProviderStatus(AgentProviderState.Ready, executable, version, CodexAgentProvider.Digest("claude-code", identity), text);
    }

    public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult(Models);

    /// <summary>Claude Code reports usage only alongside an answer: the latest one it reported, or null.</summary>
    public Task<AgentUsage?> ReadUsageAsync(CancellationToken cancellationToken)
    {
        lock (_sync) return Task.FromResult(_usage);
    }

    public async Task<AgentTurnResult> AskAsync(AgentTurn turn, AgentToolSession tools, IIntelligenceListener listener, CancellationToken cancellationToken)
    {
        var executable = _executable ?? throw new AgentProviderException("Claude Code isn't set up yet. Check it in Settings › Intelligence.",
            IntelligenceFailure.Tools, AgentProviderState.NotInstalled);
        Directory.CreateDirectory(_workingDirectory);
        var files = Path.Combine(Path.GetTempPath(), "dashynms-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(files);
        var instructions = Path.Combine(files, "instructions.md");
        var mcp = Path.Combine(files, "mcp.json");
        try
        {
            await File.WriteAllTextAsync(instructions, turn.Instructions, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(mcp, McpConfiguration(tools.Endpoint).ToJsonString(), cancellationToken).ConfigureAwait(false);
            var environment = new Dictionary<string, string?> { [TokenVariable] = tools.Token };
            var process = AgentProcess.Start(executable, Arguments(turn, instructions, mcp), _workingDirectory, environment);
            lock (_sync) _running.Add(process);
            try
            {
                return await RunAsync(process, turn, listener, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync) _running.Remove(process);
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(files, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder: it holds no credentials.
            }
        }
    }

    /// <summary>
    /// The command line: Claude Code's documented options only, all plain
    /// values - the question is written to standard input and the
    /// instructions and MCP configuration are files.
    /// </summary>
    internal static List<string> Arguments(AgentTurn turn, string instructionsFile, string mcpFile)
    {
        var args = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--setting-sources", "", "--tools", turn.WebSearch ? WebSearchTool : "", "--disable-slash-commands",
            "--strict-mcp-config", "--mcp-config", mcpFile, "--allowedTools", turn.WebSearch ? "mcp__" + BridgeServerName + "," + WebSearchTool : "mcp__" + BridgeServerName,
            "--permission-mode", "dontAsk", "--permission-prompts", "none",
            "--system-prompt-file", instructionsFile, "--model", turn.Model,
        };
        if (turn.Effort is { } effort) args.AddRange(["--effort", effort]);
        if (turn.Temporary) args.Add("--no-session-persistence");
        else if (turn.Session is { } session) args.AddRange(["--resume", session]);
        return args;
    }

    /// <summary>The bridge as an HTTP MCP server, its token referenced from the environment rather than written down.</summary>
    internal static JsonObject McpConfiguration(Uri endpoint) => new()
    {
        ["mcpServers"] = new JsonObject
        {
            [BridgeServerName] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = endpoint.AbsoluteUri,
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer ${" + TokenVariable + "}" },
            },
        },
    };

    private async Task<AgentTurnResult> RunAsync(AgentProcess process, AgentTurn turn, IIntelligenceListener listener, CancellationToken cancellationToken)
    {
        await process.WriteLineAsync((turn.Preamble ?? string.Empty) + turn.Question, cancellationToken).ConfigureAwait(false);
        process.CloseInput();

        var stream = new StreamState(turn.Session) { WebSearch = turn.WebSearch };
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
                    _logger.LogWarning("Intelligence: a Claude Code answer was stopped ({Reason})", failure.Failure);
                    throw failure;
                }

                if (stream.Usage is { } usage)
                {
                    lock (_sync) _usage = usage;
                }

                if (stream.Finished) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.Finished)
        {
            if (turn.Session is not null && process.StderrTail.Contains("No conversation found", StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentProviderException("Claude Code no longer has this conversation.", IntelligenceFailure.Model) { SessionLost = true };
            }

            throw new AgentProviderException("Claude Code stopped before it finished. Try again.", IntelligenceFailure.Model);
        }

        if (!string.IsNullOrWhiteSpace(stream.Answer)) listener.OnAnswerCompleted(stream.Answer);
        return new AgentTurnResult(turn.Temporary ? null : stream.Session, stream.Usage);
    }

    /// <summary>One stream-json message: text, the tools Claude Code loaded, usage, or the result.</summary>
    internal static AgentProviderException? Read(JsonObject message, StreamState stream, IIntelligenceListener listener)
    {
        switch (message["type"]?.ToString())
        {
            case "system" when message["subtype"]?.ToString() == "init":
                stream.Session = message["session_id"]?.ToString() ?? stream.Session;
                return CheckSession(message, stream.WebSearch);

            case "stream_event" when message["parent_tool_use_id"] is null:
                var delta = message["event"]?["delta"];
                if (message["event"]?["type"]?.ToString() == "content_block_delta" && delta?["type"]?.ToString() == "text_delta"
                    && delta["text"]?.ToString() is { Length: > 0 } text)
                {
                    listener.OnText(text);
                }

                return null;

            case "assistant":
                foreach (var block in message["message"]?["content"] as JsonArray ?? new JsonArray())
                {
                    if (block?["type"]?.ToString() != "tool_use" || block["name"]?.ToString() is not { } tool) continue;
                    if (stream.WebSearch && tool == WebSearchTool)
                    {
                        // Shown as activity, as ChatGPT's searches are; finished when its result arrives.
                        if (block["id"]?.ToString() is { } id) stream.Searches.Add(id);
                        listener.OnToolStarted("web_search", "{}");
                        continue;
                    }

                    if (!IsBridgeTool(tool))
                    {
                        return new AgentProviderException(
                            "Claude Code tried to use one of its own tools, which DashyNMS doesn't allow in Intelligence chats, so the answer was stopped. Try asking again.",
                            IntelligenceFailure.Tools);
                    }
                }

                return null;

            case "user":
                foreach (var block in message["message"]?["content"] as JsonArray ?? new JsonArray())
                {
                    if (block?["type"]?.ToString() == "tool_result" && block["tool_use_id"]?.ToString() is { } id && stream.Searches.Remove(id))
                    {
                        listener.OnToolFinished("web_search", block["is_error"]?.GetValue<bool>() == true);
                    }
                }

                return null;

            case "rate_limit_event":
                stream.Usage = Usage(message["rate_limit_info"]) ?? stream.Usage;
                return null;

            case "result":
                stream.Finished = true;
                stream.Session = message["session_id"]?.ToString() ?? stream.Session;
                if (message["is_error"]?.GetValue<bool>() == true || message["subtype"]?.ToString() is { } subtype && subtype != "success")
                {
                    return Failure(message["result"]?.ToString(), message["subtype"]?.ToString(), stream.Usage);
                }

                stream.Answer = message["result"]?.ToString();
                return null;
        }

        return null;
    }

    /// <summary>
    /// Claude Code's own account of the session it started: only DashyNMS's
    /// tools, and DashyNMS's server connected. Anything else and it isn't asked.
    /// </summary>
    private static AgentProviderException? CheckSession(JsonObject init, bool webSearch)
    {
        var tools = (init["tools"] as JsonArray ?? new JsonArray()).Select(t => t?.ToString() ?? string.Empty).ToList();
        if (tools.Any(t => !IsBridgeTool(t) && !(webSearch && t == WebSearchTool)))
        {
            return new AgentProviderException(
                "Claude Code started with tools of its own, which DashyNMS doesn't allow in Intelligence chats, so it wasn't asked. Update Claude Code, then try again.",
                IntelligenceFailure.Tools);
        }

        var bridge = (init["mcp_servers"] as JsonArray ?? new JsonArray()).FirstOrDefault(s => s?["name"]?.ToString() == BridgeServerName);
        if (bridge?["status"]?.ToString() != "connected" || !tools.Any(IsBridgeTool))
        {
            return new AgentProviderException(
                "Claude Code couldn't reach DashyNMS's network tools, so it wasn't asked to answer. Try again; if it keeps happening, restart DashyNMS.",
                IntelligenceFailure.Tools);
        }

        return null;
    }

    private static bool IsBridgeTool(string tool) => tool.StartsWith("mcp__" + BridgeServerName + "__", StringComparison.Ordinal);

    /// <summary>Claude Code's plan limits as measured usage: its five-hour and weekly windows, as fractions of each.</summary>
    internal static AgentUsage? Usage(JsonNode? info)
    {
        if (info is not JsonObject limits) return null;
        var windows = new List<AgentUsageWindow>();
        if (limits["unifiedWindows"] is JsonObject unified)
        {
            foreach (var (key, value) in unified)
            {
                if (value is not JsonObject window) continue;
                double? used = window["utilization"] is JsonValue u && u.TryGetValue<double>(out var fraction) ? Math.Round(fraction * 100, 1) : null;
                DateTimeOffset? resets = window["resetsAt"] is JsonValue r && r.TryGetValue<long>(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
                windows.Add(new AgentUsageWindow(WindowLabel(key), used, resets));
            }
        }

        var reached = limits["status"]?.ToString() == "rejected";
        if (windows.Count == 0 && !reached) return null;
        return new AgentUsage(windows, DateTimeOffset.UtcNow, reached);
    }

    internal static string WindowLabel(string key) => key switch
    {
        "five_hour" => "5-hour",
        "seven_day" => "weekly",
        "seven_day_opus" => "weekly Opus",
        "seven_day_sonnet" => "weekly Sonnet",
        _ => key.Replace('_', ' '),
    };

    /// <summary>Claude Code's error result, worded for the person, with what to do next.</summary>
    internal static AgentProviderException Failure(string? text, string? subtype, AgentUsage? usage)
    {
        var detail = text ?? string.Empty;
        if (detail.Contains("/login", StringComparison.OrdinalIgnoreCase) || detail.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("authentication", StringComparison.OrdinalIgnoreCase) || detail.Contains("OAuth", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderException(
                "Claude Code isn't signed in any more. Run claude auth login in a terminal, then choose Check again in Settings › Intelligence.",
                IntelligenceFailure.PlanUsage, AgentProviderState.SignedOut);
        }

        if (usage?.LimitReached == true || detail.Contains("usage limit", StringComparison.OrdinalIgnoreCase) || detail.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderException(
                "Claude Code has reached a usage limit for this account. Check its usage in Settings › Intelligence, then try again once it resets.",
                IntelligenceFailure.PlanUsage, usage: usage ?? new AgentUsage(Array.Empty<AgentUsageWindow>(), DateTimeOffset.UtcNow, LimitReached: true));
        }

        if (detail.Contains("usage credits", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderException(
                "This model needs usage credits on your Claude plan. Choose another model in the picker, or add credits in your Claude account.",
                IntelligenceFailure.PlanUsage);
        }

        if (subtype == "error_max_turns")
        {
            return new AgentProviderException("That needed more lookups than one question is allowed. Try asking something narrower.", IntelligenceFailure.Tools);
        }

        if (detail.Contains("model", StringComparison.OrdinalIgnoreCase) && (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) || detail.Contains("access", StringComparison.OrdinalIgnoreCase)))
        {
            return new AgentProviderException("This account can't use the chosen model. Choose another in the model picker.", IntelligenceFailure.Model);
        }

        return new AgentProviderException("Claude Code couldn't answer. Try again.", IntelligenceFailure.Model);
    }

    public async Task RetireAsync()
    {
        List<AgentProcess> running;
        lock (_sync) running = _running.ToList();
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

        return string.Join("\n", lines);
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex Version();

    /// <summary>What one question's stream has said so far.</summary>
    internal sealed class StreamState(string? session)
    {
        public string? Session { get; set; } = session;

        public string? Answer { get; set; }

        public AgentUsage? Usage { get; set; }

        public bool Finished { get; set; }

        /// <summary>Web search was allowed for this question.</summary>
        public bool WebSearch { get; init; }

        /// <summary>Web searches started and not yet answered.</summary>
        public HashSet<string> Searches { get; } = new(StringComparer.Ordinal);
    }
}

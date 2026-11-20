using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
/// Codex as a Desktop Intelligence provider (#332), through its supported
/// app-server protocol: JSON-RPC over stdio to <c>codex app-server</c>, using
/// whatever account Codex is already signed in with.
/// </summary>
/// <remarks>
/// <para>
/// Each app-server is started for DashyNMS with Codex's own tools switched
/// off - shell, file edits, apps, plugins, browser, computer use, image
/// generation, sub-agents and hooks, and web search unless Settings allows
/// it - and the user's MCP servers
/// disabled, leaving only DashyNMS's read-only tools through
/// <see cref="AgentToolBridge"/>. Threads run read-only with approvals never
/// granted: anything that still asks is declined, and a turn that starts a
/// Codex tool anyway is stopped. The user's own configuration file is read by
/// Codex but never changed; these settings are command-line overrides for
/// DashyNMS's process only.
/// </para>
/// <para>
/// The process lives while Intelligence uses it and stops after
/// <see cref="IdleTimeout"/>. Its bridge token is in its environment, never on
/// its command line; a new bridge session means a new process.
/// </para>
/// </remarks>
public sealed partial class CodexAgentProvider : IAgentProvider
{
    /// <summary>How long an unused app-server is kept before it's stopped.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The MCP server name DashyNMS uses in Codex. Distinct from anything a user
    /// is likely to have registered - a "dashynms" of their own would otherwise
    /// have these settings merged into it.
    /// </summary>
    internal const string BridgeServerName = "dashynms_desktop";

    internal const string TokenVariable = "DASHYNMS_MCP_TOKEN";

    /// <summary>
    /// Codex features that act outside the network question - all off where this
    /// Codex knows them. <c>code_mode_host</c> stays on: current Codex calls MCP
    /// tools through it, and with the shell and every other tool off it can reach
    /// only DashyNMS's. Any Codex tool item that still starts ends the turn.
    /// </summary>
    internal static readonly string[] DisabledFeatures =
    [
        "shell_tool", "unified_exec", "apps", "plugins", "remote_plugin", "image_generation", "multi_agent", "multi_agent_v2",
        "browser_use", "browser_use_external", "computer_use", "in_app_browser", "in_app_local_automation", "hooks", "codex_hooks",
        "view_image", "memories", "skill_mcp_dependency_install", "tool_suggest", "goals",
        "workspace_dependencies", "plugin_discovery_prefetch", "sleep_tool",
    ];

    /// <summary>Codex items that are its own tools: none may run in a DashyNMS chat.</summary>
    private static readonly HashSet<string> ForbiddenItems = new(StringComparer.Ordinal)
    {
        "commandExecution", "fileChange", "dynamicToolCall", "collabAgentToolCall", "subAgentActivity", "webSearch", "imageView", "imageGeneration",
    };

    private readonly string _workingDirectory;
    private readonly ILogger _logger;
    private readonly Func<IEnumerable<string>> _extraCandidates;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _executable;
    private Server? _server;
    private CancellationTokenSource? _idle;

    /// <param name="workingDirectory">An empty folder of DashyNMS's own for Codex to run in, so it never sees a project.</param>
    public CodexAgentProvider(string workingDirectory, ILogger logger, Func<IEnumerable<string>>? extraCandidates = null)
    {
        _workingDirectory = workingDirectory;
        _logger = logger;
        _extraCandidates = extraCandidates ?? BundledCandidates;
    }

    public string Id => AppSettings.IntelligenceProviderCodex;

    public string Name => "Codex";

    public string Company => "OpenAI";

    public AgentCapabilities Capabilities { get; } = new(WebSearch: true, Images: true, Usage: true);

    public async Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken)
    {
        var executable = AgentExecutables.Find(executablePath, "codex", _extraCandidates());
        if (executable is not null && AgentProcess.IsShim(executable) && NativeBehindShim(executable) is { } native) executable = native;
        if (executable is null) return new AgentProviderStatus(AgentProviderState.NotInstalled);
        var versionLine = await AgentExecutables.VersionAsync(executable, cancellationToken).ConfigureAwait(false);
        if (versionLine is null)
        {
            return new AgentProviderStatus(AgentProviderState.NotInstalled, executable,
                Detail: "DashyNMS found Codex but it didn't run. Reinstall it, or choose another executable below, then choose Check again.");
        }

        var version = Version().Match(versionLine) is { Success: true } m ? m.Value : versionLine;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_executable != executable) await StopServerAsync().ConfigureAwait(false);
            _executable = executable;
            var server = await ServerAsync(token: null, webSearch: false, cancellationToken).ConfigureAwait(false);
            var account = await server.Rpc.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, cancellationToken).WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            ScheduleIdleStop();
            return Status(executable, version, account);
        }
        catch (Exception ex) when (ex is JsonRpcException or AgentProviderException or TimeoutException or IOException)
        {
            _logger.LogWarning("Intelligence: Codex app-server check failed ({ErrorType})", ex.GetType().Name);
            await StopServerAsync().ConfigureAwait(false);
            return new AgentProviderStatus(AgentProviderState.Unavailable, executable, version,
                Detail: $"Codex {version} didn't answer DashyNMS. Update Codex to the latest version, then choose Check again.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Where Codex's sign-in stands. The account key is a digest of Codex's
    /// account id (or email, or "API key"), so chats are kept per account
    /// without the account's name appearing in a folder name.
    /// </summary>
    internal static AgentProviderStatus Status(string executable, string version, JsonNode? read)
    {
        var account = read?["account"] as JsonObject;
        if (account is null)
        {
            return new AgentProviderStatus(AgentProviderState.SignedOut, executable, version);
        }

        var type = account["type"]?.ToString();
        var email = account["email"]?.ToString();
        var plan = account["planType"]?.ToString();
        var id = read?["workspaceRouting"]?["chatgptAccountId"]?.ToString();
        var identity = type == "apiKey" ? "api-key" : id ?? email ?? "chatgpt";
        var text = type == "apiKey"
            ? "Using an OpenAI API key - usage is billed to that API account"
            : "Signed in as " + (email ?? "a ChatGPT account") + (plan is { Length: > 0 } ? $" (ChatGPT {Capitalise(plan)})" : string.Empty);
        return new AgentProviderStatus(AgentProviderState.Ready, executable, version, Digest("codex", identity), text);
    }

    public async Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var server = await CurrentServerAsync(cancellationToken).ConfigureAwait(false);
        var models = new List<(AgentModel Model, bool IsDefault)>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;
            var result = await server.Rpc.RequestAsync("model/list", parameters, cancellationToken).WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            foreach (var item in result?["data"] as JsonArray ?? new JsonArray())
            {
                if (item is not JsonObject model || model["hidden"]?.GetValue<bool>() == true || model["model"]?.ToString() is not { Length: > 0 } id) continue;
                models.Add((Model(model, id), model["isDefault"]?.GetValue<bool>() == true));
            }

            cursor = result?["nextCursor"]?.ToString();
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return models.OrderByDescending(m => m.IsDefault).Select(m => m.Model).ToList();
    }

    /// <summary>
    /// A listed model, with the reasoning levels DashyNMS offers: "ultra"
    /// delegates to sub-agents, which these chats don't allow, so it's left out.
    /// </summary>
    internal static AgentModel Model(JsonObject model, string id)
    {
        var efforts = (model["supportedReasoningEfforts"] as JsonArray ?? new JsonArray())
            .Select(e => e?["reasoningEffort"]?.ToString())
            .Where(e => e is { Length: > 0 } && e != "ultra")
            .Select(e => e!)
            .ToList();
        var fallback = model["defaultReasoningEffort"]?.ToString();
        return new AgentModel(id, model["displayName"]?.ToString() ?? id, efforts,
            fallback is not null && efforts.Contains(fallback) ? fallback : null, model["description"]?.ToString());
    }

    public async Task<AgentUsage?> ReadUsageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var server = await CurrentServerAsync(cancellationToken).ConfigureAwait(false);
            var result = await server.Rpc.RequestAsync("account/rateLimits/read", null, cancellationToken).WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            return Usage(result?["rateLimits"]);
        }
        catch (Exception ex) when (ex is JsonRpcException or AgentProviderException or TimeoutException)
        {
            // An API-key account has no plan limits to read; anything else is simply unknown for now.
            return null;
        }
    }

    /// <summary>Codex's rate-limit windows as measured usage. Absent figures stay absent.</summary>
    internal static AgentUsage? Usage(JsonNode? limits)
    {
        if (limits is not JsonObject snapshot) return null;
        var windows = new List<AgentUsageWindow>();
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (snapshot[name] is not JsonObject window) continue;
            double? used = window["usedPercent"] is JsonValue p && p.TryGetValue<double>(out var percent) ? percent : null;
            var minutes = window["windowDurationMins"] is JsonValue d && d.TryGetValue<long>(out var duration) ? duration : (long?)null;
            DateTimeOffset? resets = window["resetsAt"] is JsonValue r && r.TryGetValue<long>(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            windows.Add(new AgentUsageWindow(WindowLabel(minutes), used, resets));
        }

        if (windows.Count == 0) return null;
        var reached = snapshot["rateLimitReachedType"] is JsonValue reachedType && reachedType.ToString().Length > 0
                      || windows.Any(w => w.UsedPercent >= 100);
        return new AgentUsage(windows, DateTimeOffset.UtcNow, reached);
    }

    internal static string WindowLabel(long? minutes) => minutes switch
    {
        10080 => "weekly",
        1440 => "daily",
        { } m when m % 60 == 0 => (m / 60).ToString(CultureInfo.InvariantCulture) + "-hour",
        { } m => m.ToString(CultureInfo.InvariantCulture) + "-minute",
        null => "usage",
    };

    public async Task<AgentTurnResult> AskAsync(AgentTurn turn, AgentToolSession tools, IIntelligenceListener listener, CancellationToken cancellationToken)
    {
        Server server;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelIdleStop();
            server = await ServerAsync(tools, turn.WebSearch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await server.AskAsync(turn, listener, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ScheduleIdleStop();
        }
    }

    public async Task RetireAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelIdleStop();
            await StopServerAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await RetireAsync().ConfigureAwait(false);

    private async Task<Server> CurrentServerAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var server = _server is { IsAlive: true } running ? running : await ServerAsync(token: null, webSearch: false, cancellationToken).ConfigureAwait(false);
            ScheduleIdleStop();
            return server;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The running app-server, started anew when there's none, it has another
    /// bridge session, or web search has been turned on or off. Under the gate.
    /// </summary>
    private async Task<Server> ServerAsync(AgentToolSession? token, bool webSearch, CancellationToken cancellationToken)
    {
        if (_server is { IsAlive: true } running && (token is null || ReferenceEquals(running.Tools, token) && running.WebSearch == webSearch))
        {
            return running;
        }

        await StopServerAsync().ConfigureAwait(false);
        var executable = _executable ?? throw new AgentProviderException("Codex isn't set up yet. Check it in Settings › Intelligence.", IntelligenceFailure.Tools, AgentProviderState.NotInstalled);
        Directory.CreateDirectory(_workingDirectory);
        var features = await KnownFeaturesAsync(executable, cancellationToken).ConfigureAwait(false);
        var servers = ConfiguredMcpServers(CodexHome());
        var environment = new Dictionary<string, string?> { [TokenVariable] = token?.Token };
        var process = AgentProcess.Start(executable, Arguments(features, servers, token?.Endpoint, webSearch), _workingDirectory, environment);
        var server = new Server(process, token, webSearch, _logger);
        try
        {
            await server.InitialiseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _server = server;
        return server;
    }

    /// <summary>Codex's home folder: CODEX_HOME, else .codex in the user's home.</summary>
    private static string CodexHome()
        => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>
    /// The MCP servers the user defined in Codex's config.toml - each one
    /// switched off for DashyNMS's sessions. Servers that come from plugins or
    /// apps aren't listed there, can't be overridden, and are gone anyway with
    /// plugins and apps off. Codex's own start-up errors can name the wrong
    /// server, so this is read from the file rather than worked out from them.
    /// </summary>
    internal static IReadOnlyList<string> ConfiguredMcpServers(string codexHome)
    {
        var path = Path.Combine(codexHome, "config.toml");
        try
        {
            return File.Exists(path) ? ServersIn(File.ReadAllText(path)) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The server names in a config file's <c>[mcp_servers.NAME]</c> tables, bare or quoted.</summary>
    internal static IReadOnlyList<string> ServersIn(string toml)
        => ServerTable().Matches(toml)
            .Select(m => m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["bare"].Value)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The app-server's command line: DashyNMS's overrides only, as Codex's
    /// documented <c>--disable</c> and <c>-c</c> options. No secrets: the bridge
    /// token is read from the environment.
    /// </summary>
    internal static List<string> Arguments(IReadOnlySet<string> knownFeatures, IEnumerable<string> userServers, Uri? bridge, bool webSearch = false)
    {
        var args = new List<string> { "app-server" };
        foreach (var feature in DisabledFeatures.Where(knownFeatures.Contains))
        {
            args.Add("--disable");
            args.Add(feature);
        }

        // Live web search only when Settings allows it; nothing approved, read-only, and no project or
        // skill instructions in the prompt: a skill would only tempt the model towards tools it doesn't have here.
        args.AddRange(["-c", webSearch ? "web_search=\"live\"" : "web_search=\"disabled\"", "-c", "approval_policy=\"never\"", "-c", "sandbox_mode=\"read-only\"",
            "-c", "project_doc_max_bytes=0", "-c", "skills.include_instructions=false"]);

        // The user's MCP servers stay configured but are off for DashyNMS's sessions.
        var simple = new List<string>();
        var quoted = new List<string>();
        foreach (var name in userServers.Where(n => n != BridgeServerName).Distinct(StringComparer.Ordinal))
        {
            (SimpleName().IsMatch(name) ? simple : quoted).Add(name);
        }

        foreach (var name in simple) args.AddRange(["-c", $"mcp_servers.{name}.enabled=false"]);
        if (quoted.Count > 0)
        {
            args.AddRange(["-c", "mcp_servers={" + string.Join(",", quoted.Select(n => JsonSerializer.Serialize(n) + "={enabled=false}")) + "}"]);
        }

        if (bridge is not null)
        {
            args.AddRange(
            [
                "-c", $"mcp_servers.{BridgeServerName}.url=\"{bridge.AbsoluteUri}\"",
                "-c", $"mcp_servers.{BridgeServerName}.bearer_token_env_var=\"{TokenVariable}\"",
                "-c", $"mcp_servers.{BridgeServerName}.startup_timeout_sec=60",
                "-c", $"mcp_servers.{BridgeServerName}.tool_timeout_sec=180",

                // DashyNMS checks every call itself, and every tool reads only.
                "-c", $"mcp_servers.{BridgeServerName}.default_tools_approval_mode=\"approve\"",
            ]);
        }

        return args;
    }

    /// <summary>The feature names this Codex knows - an unknown one in --disable stops it starting.</summary>
    private static async Task<IReadOnlySet<string>> KnownFeaturesAsync(string executable, CancellationToken cancellationToken)
        => (await LinesAsync(executable, ["features", "list"], cancellationToken).ConfigureAwait(false))
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(name => name is { Length: > 0 })
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

    private static async Task<IReadOnlyList<string>> LinesAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        await using var process = AgentProcess.Start(executable, arguments, Path.GetTempPath(), redirectInput: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var line in process.ReadLinesAsync(timeout.Token).ConfigureAwait(false)) lines.Add(line);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return lines;
    }

    private async Task StopServerAsync()
    {
        if (Interlocked.Exchange(ref _server, null) is { } server) await server.DisposeAsync().ConfigureAwait(false);
    }

    private void ScheduleIdleStop()
    {
        CancelIdleStop();
        var idle = new CancellationTokenSource();
        _idle = idle;
        _ = Task.Delay(IdleTimeout, idle.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!idle.IsCancellationRequested && _server is { IsAnswering: false }) await StopServerAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }, TaskScheduler.Default);
    }

    private void CancelIdleStop()
    {
        if (Interlocked.Exchange(ref _idle, null) is { } idle)
        {
            idle.Cancel();
            idle.Dispose();
        }
    }

    /// <summary>The installs DashyNMS looks in beyond PATH: the Codex app's own copy, newest first.</summary>
    private static IEnumerable<string> BundledCandidates()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root)
                    .Select(dir => Path.Combine(dir, "codex.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>npm's codex.cmd wraps a native codex.exe; running that directly avoids cmd.exe re-reading the arguments.</summary>
    internal static string? NativeBehindShim(string shim)
    {
        var vendor = Path.Combine(Path.GetDirectoryName(shim) ?? string.Empty, "node_modules", "@openai", "codex", "vendor");
        try
        {
            return Directory.Exists(vendor)
                ? Directory.EnumerateFiles(vendor, "codex.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string Digest(string provider, string identity)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "\n" + identity)))[..32].ToLowerInvariant();

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?([-+.][0-9A-Za-z.\-]+)?")]
    private static partial Regex Version();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SimpleName();

    /// <summary>A top-level <c>[mcp_servers.NAME]</c> table header - not its <c>.env</c> or other sub-tables.</summary>
    [GeneratedRegex(@"^\s*\[\s*mcp_servers\.(?:""(?<quoted>[^""]+)""|(?<bare>[A-Za-z0-9_-]+))\s*\]", RegexOptions.Multiline)]
    private static partial Regex ServerTable();

    /// <summary>One app-server process and the turn it's answering, if any.</summary>
    internal sealed class Server : IAsyncDisposable
    {
        private readonly ILogger _logger;
        private readonly object _sync = new();
        private Turn? _turn;

        public Server(AgentProcess process, AgentToolSession? tools, bool webSearch, ILogger logger)
        {
            _logger = logger;
            Tools = tools;
            WebSearch = webSearch;
            Rpc = new JsonRpcConnection(process, OnNotificationAsync, OnRequestAsync);
        }

        public JsonRpcConnection Rpc { get; }

        /// <summary>The bridge session whose token this process holds, or null for a status-only process.</summary>
        public AgentToolSession? Tools { get; }

        /// <summary>Started with Codex's web search on.</summary>
        public bool WebSearch { get; }

        public bool IsAlive => !Rpc.IsClosed;

        public bool IsAnswering
        {
            get { lock (_sync) return _turn is not null; }
        }

        public async Task InitialiseAsync(CancellationToken cancellationToken)
        {
            await Rpc.RequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "dashynms_desktop", ["title"] = "DashyNMS", ["version"] = typeof(Server).Assembly.GetName().Version?.ToString(3) ?? "0" },
            }, cancellationToken).WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            await Rpc.NotifyAsync("initialized", null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AgentTurnResult> AskAsync(AgentTurn request, IIntelligenceListener listener, CancellationToken cancellationToken)
        {
            var turn = new Turn(listener);
            lock (_sync)
            {
                if (_turn is not null) throw new AgentProviderException("Codex is still answering another question.", IntelligenceFailure.Model);
                _turn = turn;
            }

            try
            {
                var thread = await ThreadAsync(request, cancellationToken).ConfigureAwait(false);
                turn.ThreadId = thread;
                await CheckToolsAsync(cancellationToken).ConfigureAwait(false);
                var input = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = (request.Preamble ?? string.Empty) + request.Question });
                var start = new JsonObject { ["threadId"] = thread, ["input"] = input, ["model"] = request.Model };
                if (request.Effort is { } effort) start["effort"] = effort;
                var started = await Rpc.RequestAsync("turn/start", start, cancellationToken).WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
                turn.TurnId = started?["turn"]?["id"]?.ToString();

                using var stop = cancellationToken.Register(() => _ = InterruptAsync(turn));
                var exited = Rpc.Completion;
                var finished = await Task.WhenAny(turn.Completed, exited).ConfigureAwait(false);
                if (finished != turn.Completed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new AgentProviderException("Codex stopped before it finished. Try again.", IntelligenceFailure.Model);
                }

                await turn.Completed.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var answer = turn.Answer;
                if (!string.IsNullOrWhiteSpace(answer)) listener.OnAnswerCompleted(answer);
                return new AgentTurnResult(thread, turn.Usage);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_turn, turn)) _turn = null;
                }
            }
        }

        /// <summary>The chat's thread: resumed when the chat has one, else a new one, read-only with DashyNMS's instructions.</summary>
        private async Task<string> ThreadAsync(AgentTurn request, CancellationToken cancellationToken)
        {
            var settings = new JsonObject
            {
                ["model"] = request.Model,
                ["approvalPolicy"] = "never",
                ["sandbox"] = "read-only",
                ["baseInstructions"] = request.Instructions,
            };
            if (request.Session is { } existing)
            {
                settings["threadId"] = existing;
                try
                {
                    var resumed = await Rpc.RequestAsync("thread/resume", settings, cancellationToken).WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
                    return resumed?["thread"]?["id"]?.ToString() ?? existing;
                }
                catch (JsonRpcException)
                {
                    // Gone - a temporary chat's thread after a restart, or a cleared Codex history.
                    throw new AgentProviderException("Codex no longer has this conversation.", IntelligenceFailure.Model) { SessionLost = true };
                }
            }

            settings["ephemeral"] = request.Temporary;
            var thread = await Rpc.RequestAsync("thread/start", settings, cancellationToken).WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            return thread?["thread"]?["id"]?.ToString()
                   ?? throw new AgentProviderException("Codex didn't start a conversation. Try again.", IntelligenceFailure.Model);
        }

        /// <summary>
        /// Confirms Codex has DashyNMS's tools before it's asked anything, so it
        /// never answers from nothing. Logs only counts and Codex's own error.
        /// </summary>
        private async Task CheckToolsAsync(CancellationToken cancellationToken)
        {
            var status = await Rpc.RequestAsync("mcpServerStatus/list", new JsonObject(), cancellationToken).WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            var bridge = (status?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(s => s["name"]?.ToString() == BridgeServerName);
            var count = (bridge?["tools"] as JsonObject)?.Count ?? 0;
            _logger.LogInformation("Intelligence: Codex has {Count} DashyNMS tools ({Error})", count, bridge?["toolsError"]?.ToString() ?? "no error");
            if (count == 0)
            {
                throw new AgentProviderException(
                    "Codex couldn't load DashyNMS's network tools, so it wasn't asked to answer. Try again; if it keeps happening, restart DashyNMS.",
                    IntelligenceFailure.Tools);
            }
        }

        private async Task InterruptAsync(Turn turn)
        {
            turn.Stop();
            if (turn.ThreadId is null || turn.TurnId is null) return;
            try
            {
                await Rpc.RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = turn.ThreadId, ["turnId"] = turn.TurnId }, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonRpcException or AgentProviderException or TimeoutException or IOException)
            {
                // Already finished, or the process is going anyway.
            }
        }

        private Task OnNotificationAsync(string method, JsonNode? parameters)
        {
            Turn? turn;
            lock (_sync) turn = _turn;
            if (turn is null) return Task.CompletedTask;
            switch (method)
            {
                case "item/agentMessage/delta":
                    if (parameters?["delta"]?.ToString() is { Length: > 0 } delta && turn.IsOpen) turn.Listener.OnText(delta);
                    break;

                case "item/started":
                    if (IsWebSearch(parameters?["item"]) && turn.IsOpen) turn.Listener.OnToolStarted("web_search", "{}");
                    CheckItem(turn, parameters?["item"]);
                    break;

                case "item/completed":
                    if (parameters?["item"] is JsonObject item && item["type"]?.ToString() == "agentMessage" && item["text"]?.ToString() is { } text)
                    {
                        turn.AddMessage(text);
                    }

                    if (IsWebSearch(parameters?["item"]) && turn.IsOpen) turn.Listener.OnToolFinished("web_search", isError: false);
                    CheckItem(turn, parameters?["item"]);
                    break;

                case "mcpServer/startupStatus/updated":
                    // Without its tools Codex would answer from nothing: the turn stops and says why.
                    if (parameters?["name"]?.ToString() == BridgeServerName && parameters["status"]?.ToString() is "failed" or "error")
                    {
                        _logger.LogWarning("Intelligence: Codex couldn't start DashyNMS's tools ({Reason})", parameters["failureReason"]?.ToString() ?? "no reason given");
                        turn.Fail(new AgentProviderException(
                            "Codex couldn't reach DashyNMS's network tools, so it wasn't asked to answer. Try again; if it keeps happening, restart DashyNMS.",
                            IntelligenceFailure.Tools));
                        _ = InterruptAsync(turn);
                    }

                    break;

                case "account/rateLimits/updated":
                    if (Usage(parameters?["rateLimits"]) is { } usage) turn.Usage = usage;
                    break;

                case "error":
                    if (parameters?["willRetry"]?.GetValue<bool>() != true) turn.Fail(Failure(parameters?["error"], turn.Usage));
                    break;

                case "turn/completed":
                    var status = parameters?["turn"]?["status"]?.ToString();
                    if (status == "completed") turn.Complete();
                    else if (status == "interrupted") turn.Stop();
                    else turn.Fail(Failure(parameters?["turn"]?["error"], turn.Usage));
                    break;
            }

            return Task.CompletedTask;
        }

        /// <summary>A Codex tool, or another MCP server's, starting in a DashyNMS chat ends the turn: nothing of its may run.</summary>
        private void CheckItem(Turn turn, JsonNode? item)
        {
            var type = item?["type"]?.ToString();
            var server = item?["server"]?.ToString();
            if (type is null || type == "webSearch" && WebSearch) return;
            if (!(ForbiddenItems.Contains(type) || type == "mcpToolCall" && server != BridgeServerName && !IsResourceLookup(server, item?["tool"]?.ToString()))) return;
            _logger.LogWarning("Intelligence: Codex started a {ItemType} item ({Server} {Tool}), which DashyNMS doesn't allow; the turn was stopped", type, server ?? "no server", item?["tool"]?.ToString() ?? "-");
            turn.Fail(new AgentProviderException(
                "Codex tried to use one of its own tools, which DashyNMS doesn't allow in Intelligence chats, so the answer was stopped. Try asking again.",
                IntelligenceFailure.Tools));
            _ = InterruptAsync(turn);
        }

        /// <summary>A web search item - shown as activity, as ChatGPT's searches are.</summary>
        private bool IsWebSearch(JsonNode? item) => WebSearch && item?["type"]?.ToString() == "webSearch";

        /// <summary>
        /// Codex's built-in MCP resource lookups: they read only resources the
        /// enabled servers offer, and the only enabled server is DashyNMS's
        /// bridge, which offers none.
        /// </summary>
        private static bool IsResourceLookup(string? server, string? tool)
            => server == "codex" && tool is "list_mcp_resources" or "list_mcp_resource_templates" or "read_mcp_resource";

        /// <summary>
        /// Codex's own requests. Nothing is approved: commands, file changes,
        /// extra permissions and elicitations are declined, and anything else is refused.
        /// </summary>
        private Task<JsonNode?> OnRequestAsync(string method, JsonNode? parameters)
        {
            _logger.LogInformation("Intelligence: declined Codex request {Method}", method);
            JsonNode? answer = method switch
            {
                "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" => new JsonObject { ["decision"] = "cancel" },
                "execCommandApproval" or "applyPatchApproval" => new JsonObject { ["decision"] = "abort" },
                "item/permissions/requestApproval" => new JsonObject { ["permissions"] = new JsonObject() },
                "mcpServer/elicitation/request" => new JsonObject { ["action"] = "decline" },
                _ => throw new JsonRpcException(-32601, "DashyNMS doesn't support this request."),
            };
            return Task.FromResult(answer);
        }

        /// <summary>Codex's turn error, worded for the person, with what to do next.</summary>
        internal static AgentProviderException Failure(JsonNode? error, AgentUsage? usage)
        {
            var info = error?["codexErrorInfo"];
            var code = info is JsonValue value ? value.ToString() : info is JsonObject obj ? obj.Select(p => p.Key).FirstOrDefault() : null;
            return code switch
            {
                "usageLimitExceeded" or "rateLimitExceeded" => new AgentProviderException(
                    "Codex has reached a usage limit for this account. Check its usage in Settings › Intelligence, then try again once it resets.",
                    IntelligenceFailure.PlanUsage, usage: usage ?? new AgentUsage(Array.Empty<AgentUsageWindow>(), DateTimeOffset.UtcNow, LimitReached: true)),
                "unauthorized" => new AgentProviderException(
                    "Codex isn't signed in any more. Run codex login in a terminal, then choose Check again in Settings › Intelligence.",
                    IntelligenceFailure.PlanUsage, AgentProviderState.SignedOut),
                "contextWindowExceeded" => new AgentProviderException(
                    "This conversation is too long for the model. Start a new chat to carry on.", IntelligenceFailure.Model),
                "modelNotFound" => new AgentProviderException(
                    "This account can't use the chosen model. Choose another in the model picker.", IntelligenceFailure.Model),
                "serverOverloaded" or "internalServerError" => new AgentProviderException(
                    "OpenAI couldn't answer just now. Try again in a moment.", IntelligenceFailure.Model),
                { } when code.Contains("Connection", StringComparison.Ordinal) || code.Contains("Disconnected", StringComparison.Ordinal) => new AgentProviderException(
                    "Codex lost its connection to OpenAI. Check your internet connection and try again.", IntelligenceFailure.Network),
                _ => new AgentProviderException("Codex couldn't answer. Try again.", IntelligenceFailure.Model),
            };
        }

        public async ValueTask DisposeAsync()
        {
            Turn? turn;
            lock (_sync) turn = _turn;
            turn?.Stop();
            await Rpc.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One question being answered, and how it ended.</summary>
    private sealed class Turn(IIntelligenceListener listener)
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _messages = new();

        public IIntelligenceListener Listener { get; } = listener;

        public string? ThreadId { get; set; }

        public string? TurnId { get; set; }

        public AgentUsage? Usage { get; set; }

        public Task Completed => _completed.Task;

        /// <summary>Text arriving after Stop or a failure is never shown.</summary>
        public bool IsOpen => !_completed.Task.IsCompleted;

        public string Answer
        {
            get { lock (_messages) return string.Join("\n\n", _messages.Where(m => !string.IsNullOrWhiteSpace(m))); }
        }

        public void AddMessage(string text)
        {
            if (!IsOpen) return;
            lock (_messages) _messages.Add(text);
        }

        public void Complete() => _completed.TrySetResult();

        public void Stop() => _completed.TrySetCanceled();

        public void Fail(Exception error) => _completed.TrySetException(error);
    }
}

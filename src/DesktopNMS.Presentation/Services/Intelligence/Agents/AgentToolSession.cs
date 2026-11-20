using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>
/// One agent's access to the DashyNMS tools through <see cref="AgentToolBridge"/>
/// (#316): a random bearer token the agent is given through its environment,
/// valid only until the session is disposed, and tool calls only while a
/// question is being answered. Every call runs through Core's
/// <see cref="IntelligenceToolRunner"/>, the same rules as the ChatGPT route,
/// against this app's <see cref="IIntelligenceTools"/> - so LibreNMS, Unimus and
/// Graylog credentials never reach the agent, and a retired connection's
/// results are discarded exactly as they are for ChatGPT.
/// </summary>
public sealed class AgentToolSession : IDisposable
{
    /// <summary>The most lookups one question may make - the instructions promise the same as ChatGPT's.</summary>
    public const int MaxToolCalls = IntelligenceConversation.MaxToolCalls;

    internal const string NotRun = "Not run: this question has used all its lookups. Answer from the results you have, and say what you couldn't check.";

    internal const string NoQuestion = "No question is being answered, so the DashyNMS tools aren't available.";

    private readonly AgentToolBridge _bridge;
    private readonly IIntelligenceTools _tools;
    private readonly object _sync = new();
    private Turn? _turn;
    private IReadOnlySet<string>? _offered;
    private bool _disposed;

    internal AgentToolSession(AgentToolBridge bridge, IIntelligenceTools tools, Uri endpoint)
    {
        _bridge = bridge;
        _tools = tools;
        Endpoint = endpoint;
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The bridge's URL - the same for every session; the token is what tells them apart.</summary>
    public Uri Endpoint { get; }

    /// <summary>The bearer token, handed to the agent only through its environment, never its command line.</summary>
    public string Token { get; }

    /// <summary>The MCP server name agents see; their tool names become mcp__dashynms__…</summary>
    public const string ServerName = IntelligenceConversation.ToolNamespace;

    /// <summary>
    /// Opens tool calls for one question: their activity goes to
    /// <paramref name="listener"/>, and cancelling <paramref name="cancellationToken"/>
    /// stops any running lookup. Disposing the result closes them again.
    /// </summary>
    public IDisposable BeginTurn(IIntelligenceListener listener, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var turn = new Turn(this, listener, cancellationToken);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _turn = turn;
        }

        return turn;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _turn = null;
        }

        _bridge.Remove(this);
    }

    /// <summary>
    /// Answers one MCP JSON-RPC request; null for a notification, which has no
    /// answer. Errors in a tool are results, not protocol errors, so the agent
    /// sees them and can say what it couldn't check.
    /// </summary>
    internal async Task<JsonObject?> HandleAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        if (id is null)
        {
            return null;
        }

        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(message["params"]),
                "ping" => new JsonObject(),
                "tools/list" => await ListToolsAsync(cancellationToken).ConfigureAwait(false),
                "tools/call" => await CallToolAsync(message["params"], cancellationToken).ConfigureAwait(false),
                _ => throw new MethodNotFoundException(),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (MethodNotFoundException)
        {
            return Error(id, -32601, "Method not found");
        }
        catch (Exception ex) when (ex is IntelligenceException or InvalidOperationException or JsonException)
        {
            // Network-tool start-up failures are worded for people already (IntelligenceException);
            // anything else may hold server detail, so it isn't passed on.
            return Error(id, -32603, ex is IntelligenceException ? ex.Message : "The DashyNMS tools couldn't answer.");
        }
    }

    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26"];

    private static JsonObject Initialize(JsonNode? parameters)
    {
        var requested = parameters?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var version) ? version : null;
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[1],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "DashyNMS network tools", ["version"] = typeof(AgentToolSession).Assembly.GetName().Version?.ToString(3) ?? "0" },
        };
    }

    private async Task<JsonObject> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await OfferedToolsAsync(cancellationToken).ConfigureAwait(false);
        var list = new JsonArray();
        foreach (var tool in tools)
        {
            list.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchema.GetRawText()) as JsonObject ?? new JsonObject { ["type"] = "object" },
                ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false, ["openWorldHint"] = false },
            });
        }

        return new JsonObject { ["tools"] = list };
    }

    /// <summary>The approved tools the network-tools server lists - what this session offers, and all it will run.</summary>
    private async Task<IReadOnlyList<IntelligenceTool>> OfferedToolsAsync(CancellationToken cancellationToken)
    {
        var tools = (await _tools.GetToolsAsync(cancellationToken).ConfigureAwait(false))
            .Where(t => IntelligenceToolAllowlist.Names.Contains(t.Name))
            .ToList();
        lock (_sync) _offered = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return tools;
    }

    private async Task<JsonObject> CallToolAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"] is JsonValue n && n.TryGetValue<string>(out var toolName) ? toolName : null;
        var arguments = parameters?["arguments"] is { } args ? args.ToJsonString() : "{}";

        Turn? turn;
        lock (_sync) turn = _turn;
        if (turn is null || turn.Token.IsCancellationRequested)
        {
            return ToolResult(NoQuestion, isError: true);
        }

        if (!turn.TryCount())
        {
            return ToolResult(NotRun, isError: true);
        }

        IReadOnlySet<string>? offered;
        lock (_sync) offered = _offered;
        offered ??= (await OfferedToolsAsync(cancellationToken).ConfigureAwait(false)).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turn.Token);
        IntelligenceToolOutcome outcome;
        try
        {
            outcome = await IntelligenceToolRunner.RunAsync(_tools, offered, name, arguments, new TurnListener(this, turn), call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ToolResult("The question was stopped, so this lookup didn't finish.", isError: true);
        }

        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.Text });
        foreach (var image in outcome.Images)
        {
            content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(image.Data), ["mimeType"] = image.MimeType });
        }

        return new JsonObject { ["content"] = content, ["isError"] = outcome.IsError };
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Error(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private sealed class MethodNotFoundException : Exception;

    /// <summary>One question's window for tool calls, and how many it has used.</summary>
    private sealed class Turn(AgentToolSession owner, IIntelligenceListener listener, CancellationToken token) : IDisposable
    {
        private int _calls;

        public IIntelligenceListener Listener { get; } = listener;

        public CancellationToken Token { get; } = token;

        public bool TryCount() => Interlocked.Increment(ref _calls) <= MaxToolCalls;

        /// <summary>Activity from a call that outlived its question is dropped, never shown on the next one.</summary>
        public bool IsCurrent
        {
            get { lock (owner._sync) return ReferenceEquals(owner._turn, this); }
        }

        public void Dispose()
        {
            lock (owner._sync)
            {
                if (ReferenceEquals(owner._turn, this)) owner._turn = null;
            }
        }
    }

    /// <summary>Passes a call's activity on only while its question is still the one being answered.</summary>
    private sealed class TurnListener(AgentToolSession owner, Turn turn) : IIntelligenceListener
    {
        private IIntelligenceListener? Current => turn.IsCurrent && !owner._disposed ? turn.Listener : null;

        public void OnText(string delta) { }

        public void OnToolStarted(string name, string arguments) => Current?.OnToolStarted(name, arguments);

        public void OnToolFinished(string name, bool isError) => Current?.OnToolFinished(name, isError);

        public void OnImage(IntelligenceImage image) => Current?.OnImage(image);

        public void OnStatusCard(IntelligenceStatusCard card) => Current?.OnStatusCard(card);

        public void OnToolObserved(IntelligenceToolObservation observation) => Current?.OnToolObserved(observation);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Services.Intelligence.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// The loopback MCP bridge agents reach DashyNMS's tools through (#316),
/// over real HTTP: who may call it, and that every call follows the same
/// rules as the ChatGPT route.
/// </summary>
public sealed class AgentToolBridgeTests : IAsyncLifetime
{
    private readonly FakeTools _tools = new();
    private AgentToolBridge _bridge = null!;
    private AgentToolSession _session = null!;
    private readonly HttpClient _http = new();

    public async Task InitializeAsync()
    {
        _bridge = new AgentToolBridge(_tools, NullLogger.Instance);
        _session = await _bridge.OpenSessionAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _bridge.DisposeAsync();
    }

    [Fact]
    public void Listens_only_on_the_loopback_address()
    {
        Assert.Equal("127.0.0.1", _session.Endpoint.Host);
        Assert.Equal("/mcp", _session.Endpoint.AbsolutePath);
    }

    [Fact]
    public async Task Refuses_a_missing_or_wrong_token()
    {
        using var none = await PostAsync(Request("tools/list"), token: null);
        using var wrong = await PostAsync(Request("tools/list"), token: "not-the-token");

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Refuses_browser_origins_and_other_host_names()
    {
        using var fromPage = await PostAsync(Request("tools/list"), configure: r => r.Headers.Add("Origin", "https://example.net"));
        using var rebound = await PostAsync(Request("tools/list"), configure: r => r.Headers.Host = "attacker.example.net:" + _session.Endpoint.Port);

        Assert.Equal(HttpStatusCode.Forbidden, fromPage.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rebound.StatusCode);
    }

    [Fact]
    public async Task Answers_GET_with_method_not_allowed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _session.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Token);
        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Initialises_and_lists_only_approved_tools_as_read_only()
    {
        var init = await RpcAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25" });
        var list = await RpcAsync("tools/list");

        Assert.Equal("2025-11-25", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("dashynms", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        var tools = list["result"]!["tools"]!.AsArray();
        Assert.Equal(["network_overview"], tools.Select(t => t!["name"]!.GetValue<string>()));
        Assert.True(tools[0]!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_notification_is_accepted_without_an_answer()
    {
        using var response = await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Refuses_tool_calls_outside_a_question()
    {
        var result = await CallAsync("network_overview");

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal(AgentToolSession.NoQuestion, Text(result));
        Assert.Equal(0, _tools.Calls);
    }

    [Fact]
    public async Task Runs_a_call_during_a_question_and_reports_it_to_that_question()
    {
        var listener = new Listener();
        using (_session.BeginTurn(listener, CancellationToken.None))
        {
            var result = await CallAsync("network_overview");

            Assert.False(result["isError"]!.GetValue<bool>());
            Assert.Equal("""{"status":"ok","summary":"42 devices"}""", Text(result));
        }

        Assert.Equal(["network_overview"], listener.Started);
        Assert.Equal("42 devices", Assert.Single(listener.Observations).Summary);
    }

    [Fact]
    public async Task Refuses_tools_the_allowlist_does_not_name()
    {
        using var turn = _session.BeginTurn(new Listener(), CancellationToken.None);

        var result = await CallAsync("delete_device");

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("There is no tool called", Text(result));
        Assert.Equal(0, _tools.Calls);
    }

    [Fact]
    public async Task Stops_running_lookups_once_a_question_has_used_its_allowance()
    {
        using var turn = _session.BeginTurn(new Listener(), CancellationToken.None);
        for (var i = 0; i < AgentToolSession.MaxToolCalls; i++) await CallAsync("network_overview");

        var over = await CallAsync("network_overview");

        Assert.Equal(AgentToolSession.NotRun, Text(over));
        Assert.Equal(AgentToolSession.MaxToolCalls, _tools.Calls);
    }

    [Fact]
    public async Task A_stopped_question_refuses_further_calls()
    {
        using var stop = new CancellationTokenSource();
        using var turn = _session.BeginTurn(new Listener(), stop.Token);
        await stop.CancelAsync();

        var result = await CallAsync("network_overview");

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal(0, _tools.Calls);
    }

    [Fact]
    public async Task A_disposed_session_token_stops_working()
    {
        _session.Dispose();

        using var response = await PostAsync(Request("tools/list"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _bridge.SessionCount);
    }

    [Fact]
    public async Task Each_session_has_its_own_token()
    {
        using var other = await _bridge.OpenSessionAsync(CancellationToken.None);

        Assert.NotEqual(_session.Token, other.Token);
        Assert.Equal(_session.Endpoint, other.Endpoint);
        Assert.True(_session.Token.Length >= 43);
    }

    [Fact]
    public async Task Sends_graphs_as_MCP_image_content()
    {
        _tools.Result = new IntelligenceToolResult("graph", IsError: false, [new IntelligenceImage([1, 2, 3], "image/png")]);
        using var turn = _session.BeginTurn(new Listener(), CancellationToken.None);

        var result = await CallAsync("network_overview");

        var image = result["content"]!.AsArray()[1]!;
        Assert.Equal("image", image["type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), image["data"]!.GetValue<string>());
    }

    private async Task<JsonObject> CallAsync(string tool)
        => (JsonObject)(await RpcAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() }))["result"]!;

    private async Task<JsonObject> RpcAsync(string method, JsonObject? parameters = null)
    {
        using var response = await PostAsync(Request(method, parameters));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static JsonObject Request(string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject(),
    };

    private Task<HttpResponseMessage> PostAsync(JsonObject body, string? token = "", Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _session.Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        token = token == "" ? _session.Token : token;
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        configure?.Invoke(request);
        return _http.SendAsync(request);
    }

    private static string Text(JsonObject result) => result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();

    private sealed class FakeTools : IIntelligenceTools
    {
        public int Calls;

        public IntelligenceToolResult Result { get; set; } = new("""{"status":"ok","summary":"42 devices"}""", IsError: false);

        public Task<IReadOnlyList<IntelligenceTool>> GetToolsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IntelligenceTool>>(
            [
                new("network_overview", "Counts", JsonSerializer.SerializeToElement(new { type = "object" })),
                new("delete_device", "Not approved", JsonSerializer.SerializeToElement(new { type = "object" })),
            ]);

        public Task<IntelligenceToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Result);
        }
    }

    private sealed class Listener : IIntelligenceListener
    {
        public List<string> Started { get; } = new();
        public List<IntelligenceToolObservation> Observations { get; } = new();
        public void OnText(string delta) { }
        public void OnToolStarted(string name, string arguments) => Started.Add(name);
        public void OnToolFinished(string name, bool isError) { }
        public void OnImage(IntelligenceImage image) { }
        public void OnToolObserved(IntelligenceToolObservation observation) => Observations.Add(observation);
    }
}

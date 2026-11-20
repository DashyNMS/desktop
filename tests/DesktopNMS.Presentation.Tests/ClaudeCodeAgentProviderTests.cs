using System.Text.Json.Nodes;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Services.Intelligence.Agents;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// The Claude Code adapter's reading of its headless stream-json output and
/// its command line (#317), from messages shaped as Claude Code 2.1 sends
/// them, with synthetic values.
/// </summary>
public sealed class ClaudeCodeAgentProviderTests
{
    private const string Init = """
        {"type":"system","subtype":"init","session_id":"session-1","tools":["mcp__dashynms__network_overview"],
         "mcp_servers":[{"name":"dashynms","status":"connected","source":"dynamic"}],"model":"claude-example"}
        """;

    [Fact]
    public void Every_model_offers_the_reasoning_levels()
        => Assert.All(ClaudeCodeAgentProvider.Models, model => Assert.Equal(["low", "medium", "high", "xhigh", "max"], model.Efforts));

    [Fact]
    public void A_claude_ai_login_is_ready_with_a_digest_for_its_chats()
    {
        var status = ClaudeCodeAgentProvider.Status("claude", "2.1.302", JsonNode.Parse("""
            {"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"someone@example.net","orgId":"org-1","subscriptionType":"pro"}
            """));

        Assert.True(status.IsReady);
        Assert.Equal("Signed in as someone@example.net (Claude Pro)", status.AccountText);
        Assert.Equal(32, status.AccountKey!.Length);
        Assert.DoesNotContain("example.net", status.AccountKey);
    }

    [Fact]
    public void Separate_accounts_have_separate_keys()
    {
        var one = ClaudeCodeAgentProvider.Status("claude", "2", JsonNode.Parse("""{"loggedIn":true,"authMethod":"claude.ai","email":"one@example.net","orgId":"org-1"}"""));
        var two = ClaudeCodeAgentProvider.Status("claude", "2", JsonNode.Parse("""{"loggedIn":true,"authMethod":"claude.ai","email":"two@example.net","orgId":"org-1"}"""));

        Assert.NotEqual(one.AccountKey, two.AccountKey);
    }

    [Fact]
    public void Logged_out_means_signed_out()
        => Assert.Equal(AgentProviderState.SignedOut, ClaudeCodeAgentProvider.Status("claude", "2", JsonNode.Parse("""{"loggedIn":false}""")).State);

    [Fact]
    public void An_API_key_says_where_usage_is_billed()
    {
        var status = ClaudeCodeAgentProvider.Status("claude", "2", JsonNode.Parse("""{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty"}"""));

        Assert.Contains("API key", status.AccountText);
    }

    [Fact]
    public void Streams_text_and_takes_the_final_answer_and_session_from_the_result()
    {
        var listener = new Listener();
        var stream = new ClaudeCodeAgentProvider.StreamState(null);

        Assert.Null(Read(Init, stream, listener));
        Assert.Null(Read("""{"type":"stream_event","parent_tool_use_id":null,"event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Two "}}}""", stream, listener));
        Assert.Null(Read("""{"type":"stream_event","parent_tool_use_id":null,"event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"are down."}}}""", stream, listener));
        Assert.Null(Read("""{"type":"result","subtype":"success","is_error":false,"result":"Two are down.","session_id":"session-1"}""", stream, listener));

        Assert.Equal("Two are down.", string.Concat(listener.Text));
        Assert.True(stream.Finished);
        Assert.Equal("Two are down.", stream.Answer);
        Assert.Equal("session-1", stream.Session);
    }

    [Fact]
    public void A_session_with_its_own_tools_is_never_asked()
    {
        var failure = Read("""
            {"type":"system","subtype":"init","session_id":"s","tools":["Bash","mcp__dashynms__network_overview"],"mcp_servers":[{"name":"dashynms","status":"connected"}]}
            """, new(null), new Listener());

        Assert.NotNull(failure);
        Assert.Equal(IntelligenceFailure.Tools, failure.Failure);
    }

    [Fact]
    public void A_session_without_DashyNMS_tools_is_never_asked()
    {
        var failure = Read("""{"type":"system","subtype":"init","session_id":"s","tools":[],"mcp_servers":[{"name":"dashynms","status":"failed"}]}""", new(null), new Listener());

        Assert.NotNull(failure);
        Assert.Contains("couldn't reach", failure.Message);
    }

    [Fact]
    public void A_call_to_another_tool_stops_the_answer()
    {
        var failure = Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{}}]}}""", new(null), new Listener());

        Assert.NotNull(failure);
    }

    [Fact]
    public void A_DashyNMS_tool_call_is_allowed()
        => Assert.Null(Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__dashynms__device_details","input":{}}]}}""", new(null), new Listener()));

    [Fact]
    public void Plan_windows_become_measured_usage()
    {
        var usage = ClaudeCodeAgentProvider.Usage(JsonNode.Parse("""
            {"status":"allowed","resetsAt":1795213800,"rateLimitType":"five_hour",
             "unifiedWindows":{"five_hour":{"utilization":0.17,"resetsAt":1795213800},"seven_day":{"utilization":0.71,"resetsAt":1795424400}}}
            """));

        Assert.Equal([("5-hour", 17.0), ("weekly", 71.0)], usage!.Windows.Select(w => (w.Label, w.UsedPercent!.Value)));
        Assert.False(usage.LimitReached);
    }

    [Fact]
    public void A_rejected_request_is_a_reached_limit()
        => Assert.True(ClaudeCodeAgentProvider.Usage(JsonNode.Parse("""{"status":"rejected"}"""))!.LimitReached);

    [Fact]
    public void No_limit_information_means_no_usage()
        => Assert.Null(ClaudeCodeAgentProvider.Usage(JsonNode.Parse("""{"status":"allowed"}""")));

    [Theory]
    [InlineData("Invalid API key · Please run /login", IntelligenceFailure.PlanUsage)]
    [InlineData("Claude AI usage limit reached", IntelligenceFailure.PlanUsage)]
    [InlineData("Something unexpected", IntelligenceFailure.Model)]
    public void Errors_are_worded_for_people(string text, IntelligenceFailure failure)
    {
        var ex = ClaudeCodeAgentProvider.Failure(text, "success", null);

        Assert.Equal(failure, ex.Failure);
        Assert.DoesNotContain(text, ex.Message);
    }

    [Fact]
    public void A_lost_login_says_how_to_sign_in()
    {
        var ex = ClaudeCodeAgentProvider.Failure("Please run /login", null, null);

        Assert.Equal(AgentProviderState.SignedOut, ex.State);
        Assert.Contains("claude auth login", ex.Message);
    }

    [Fact]
    public void The_command_line_locks_the_session_down_and_holds_no_question_or_secret()
    {
        var args = ClaudeCodeAgentProvider.Arguments(
            new AgentTurn("What is down?", "sonnet", "high", "instructions", "session-1", Temporary: false), "instructions.md", "mcp.json");

        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("", args[args.IndexOf("--setting-sources") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--disable-slash-commands", args);
        Assert.Equal("mcp__dashynms", args[args.IndexOf("--allowedTools") + 1]);
        Assert.Equal("dontAsk", args[args.IndexOf("--permission-mode") + 1]);
        Assert.Equal("high", args[args.IndexOf("--effort") + 1]);
        Assert.Equal("session-1", args[args.IndexOf("--resume") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("What is down", StringComparison.Ordinal) || a == "instructions");
    }

    [Fact]
    public void Web_search_adds_only_the_search_tool()
    {
        var args = ClaudeCodeAgentProvider.Arguments(new AgentTurn("Q", "sonnet", null, "i", null, false, WebSearch: true), "i.md", "m.json");

        Assert.Equal("WebSearch", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("mcp__dashynms,WebSearch", args[args.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("WebFetch", StringComparison.Ordinal));
    }

    [Fact]
    public void The_search_tool_is_refused_when_web_search_is_off_and_shown_when_on()
    {
        const string init = """
            {"type":"system","subtype":"init","session_id":"s","tools":["WebSearch","mcp__dashynms__network_overview"],"mcp_servers":[{"name":"dashynms","status":"connected"}]}
            """;
        const string search = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"WebSearch","input":{}}]}}""";
        const string result = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"results"}]}}""";

        Assert.NotNull(Read(init, new(null), new Listener()));
        Assert.NotNull(Read(search, new(null), new Listener()));

        var listener = new Listener();
        var stream = new ClaudeCodeAgentProvider.StreamState(null) { WebSearch = true };
        Assert.Null(Read(init, stream, listener));
        Assert.Null(Read(search, stream, listener));
        Assert.Null(Read(result, stream, listener));
        Assert.Equal(["started web_search", "finished web_search"], listener.Activity);
    }

    [Fact]
    public void Fetching_pages_stays_refused_even_with_web_search()
    {
        var stream = new ClaudeCodeAgentProvider.StreamState(null) { WebSearch = true };

        Assert.NotNull(Read("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"WebFetch","input":{}}]}}""", stream, new Listener()));
    }

    [Fact]
    public void A_temporary_chat_keeps_nothing_on_disk()
    {
        var args = ClaudeCodeAgentProvider.Arguments(new AgentTurn("Q", "haiku", null, "i", "session-1", Temporary: true), "i.md", "m.json");

        Assert.Contains("--no-session-persistence", args);
        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public void The_MCP_configuration_references_the_token_rather_than_holding_it()
    {
        var config = ClaudeCodeAgentProvider.McpConfiguration(new Uri("http://127.0.0.1:50000/mcp")).ToJsonString();

        Assert.Contains("\"type\":\"http\"", config);
        Assert.Contains("${DASHYNMS_MCP_TOKEN}", config);
    }

    private static AgentProviderException? Read(string json, ClaudeCodeAgentProvider.StreamState stream, IIntelligenceListener listener)
        => ClaudeCodeAgentProvider.Read(JsonNode.Parse(json)!.AsObject(), stream, listener);

    private sealed class Listener : IIntelligenceListener
    {
        public List<string> Text { get; } = new();
        public List<string> Activity { get; } = new();
        public void OnText(string delta) => Text.Add(delta);
        public void OnToolStarted(string name, string arguments) => Activity.Add("started " + name);
        public void OnToolFinished(string name, bool isError) => Activity.Add("finished " + name);
        public void OnImage(IntelligenceImage image) { }
    }
}

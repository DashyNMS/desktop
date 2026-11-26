using System.Text;
using System.Text.Json.Nodes;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Services.Intelligence.Agents;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// The GitHub Copilot adapter (#606): its reading of the CLI's account,
/// model and quota answers, its JSON event stream and its command line, from
/// messages shaped as Copilot CLI 1.0 sends them, with synthetic values.
/// </summary>
public sealed class CopilotAgentProviderTests
{
    private static JsonNode? Auth(string user) => JsonNode.Parse($$$"""
        {"authInfo":{"type":"user","host":"https://github.com","login":"someone","copilotUser":{{{user}}}}}
        """);

    [Fact]
    public void A_signed_in_account_is_ready_with_a_digest_for_its_chats()
    {
        var status = CopilotAgentProvider.Status("copilot", "1.0.114", Auth("""{"access_type_sku":"no_access","chat_enabled":false,"is_mcp_enabled":true,"copilot_plan":"individual"}"""));

        // The plan fields lag a sign-up, so they neither block nor name a plan.
        Assert.True(status.IsReady);
        Assert.Equal("Signed in as someone (GitHub)", status.AccountText);
        Assert.DoesNotContain("someone", status.AccountKey);
    }

    [Fact]
    public void No_account_means_signed_out()
        => Assert.Equal(AgentProviderState.SignedOut, CopilotAgentProvider.Status("copilot", "1", JsonNode.Parse("""{"authInfo":null}""")).State);

    [Fact]
    public void MCP_turned_off_for_the_account_is_unavailable_and_says_where_to_turn_it_on()
    {
        var status = CopilotAgentProvider.Status("copilot", "1", Auth("""{"chat_enabled":true,"is_mcp_enabled":false}"""));

        Assert.Equal(AgentProviderState.Unavailable, status.State);
        Assert.Contains("github.com/settings/copilot/features", status.Detail);
        Assert.NotNull(status.AccountKey);
    }

    [Fact]
    public void Fields_the_account_call_leaves_out_never_block()
        => Assert.True(CopilotAgentProvider.Status("copilot", "1", JsonNode.Parse("""{"authInfo":{"type":"token","host":"https://github.com"}}""")).IsReady);

    [Fact]
    public void Models_come_from_the_CLI_after_Auto_without_the_ones_policy_turns_off()
    {
        var models = CopilotAgentProvider.Models(JsonNode.Parse("""
            {"models":[
              {"id":"example-large","name":"Example Large","vendor":"Example","supportedReasoningEfforts":["low","medium","high"],"defaultReasoningEffort":"medium","policy":{"state":"enabled"}},
              {"id":"example-small","name":"Example Small","policy":{"state":"unconfigured"}},
              {"id":"example-blocked","name":"Example Blocked","policy":{"state":"disabled"}},
              {"id":"auto","name":"Auto"}]}
            """));

        Assert.Equal(["auto", "example-large", "example-small"], models.Select(m => m.Id));
        Assert.Equal(["low", "medium", "high"], models[1].Efforts);
        Assert.Equal("medium", models[1].DefaultEffort);
        Assert.Empty(models[2].Efforts);
    }

    [Fact]
    public void Usage_shows_limited_quotas_and_leaves_out_unlimited_ones()
    {
        var usage = CopilotAgentProvider.Usage(JsonNode.Parse("""
            {"quotaSnapshots":{
              "premium_interactions":{"isUnlimitedEntitlement":false,"remainingPercentage":25,"resetDate":"2026-12-01T00:00:00Z","usageAllowedWithExhaustedQuota":false,"overageAllowedWithExhaustedQuota":false},
              "chat":{"isUnlimitedEntitlement":true,"remainingPercentage":100},
              "premium_extra":{"isUnlimitedEntitlement":false,"entitlementRequests":0,"remainingPercentage":0},
              "completions":{"isUnlimitedEntitlement":false,"remainingPercentage":50}}}
            """));

        var window = Assert.Single(usage!.Windows);
        Assert.Equal("monthly premium requests", window.Label);
        Assert.Equal(75, window.UsedPercent);
        Assert.False(usage.LimitReached);
    }

    [Fact]
    public void An_exhausted_quota_that_stops_work_is_a_reached_limit()
    {
        var usage = CopilotAgentProvider.Usage(JsonNode.Parse("""
            {"quotaSnapshots":{"premium_interactions":{"isUnlimitedEntitlement":false,"entitlementRequests":300,"remainingPercentage":0,"usageAllowedWithExhaustedQuota":false,"overageAllowedWithExhaustedQuota":false}}}
            """));

        Assert.True(usage!.LimitReached);
    }

    [Fact]
    public void No_quotas_means_no_usage()
        => Assert.Null(CopilotAgentProvider.Usage(JsonNode.Parse("""{"quotaSnapshots":{}}""")));

    [Fact]
    public void The_command_line_allows_only_DashyNMS_tools_and_keeps_the_question_off_it()
    {
        var args = CopilotAgentProvider.Arguments(new AgentTurn("secret question", "example-large", "high", "instructions", "session-1", Temporary: false), "mcp.json", ["mine", "dashynms"]);

        Assert.DoesNotContain(args, a => a.Contains("secret question"));
        Assert.Contains("--disable-builtin-mcps", args);
        Assert.Contains("--no-custom-instructions", args);
        Assert.Contains("--sync-session=off", args);
        Assert.Equal("@mcp.json", args[args.IndexOf("--additional-mcp-config") + 1]);
        Assert.Equal("mine", args[args.IndexOf("--disable-mcp-server") + 1]);
        Assert.Single(args, a => a == "--disable-mcp-server");
        Assert.Equal("high", args[args.IndexOf("--reasoning-effort") + 1]);
        Assert.Contains("--resume=session-1", args);
        var tools = args.Skip(args.IndexOf("--available-tools") + 1).ToList();
        Assert.Equal(IntelligenceToolAllowlist.Names.Count, tools.Count);
        Assert.All(tools, t => Assert.StartsWith("mcp:dashynms-", t));
        Assert.DoesNotContain(args, a => a.StartsWith("--allow-all", StringComparison.Ordinal) || a == "--yolo");
    }

    [Fact]
    public void A_temporary_chat_never_resumes()
        => Assert.DoesNotContain(CopilotAgentProvider.Arguments(new AgentTurn("q", "auto", null, "i", "session-1", Temporary: true), "m", []), a => a.StartsWith("--resume", StringComparison.Ordinal));

    [Fact]
    public void New_and_resumed_questions_explain_the_web_search_limitation()
    {
        var provider = new CopilotAgentProvider(Path.GetTempPath(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.False(provider.Capabilities.WebSearch);
        var instructions = IntelligenceInstructions.Build(null, null, webSearchEnabled: true, personality: null, webSearchSupported: false);
        var turn = new AgentTurn("Latest firmware?", "auto", null, instructions, null, false);
        var first = CopilotAgentProvider.Prompt(turn);
        var resumed = CopilotAgentProvider.Prompt(turn with { Session = "session-1" });

        Assert.Contains("Web search isn't available through this provider", first, StringComparison.Ordinal);
        Assert.Contains("Web search is unavailable through GitHub Copilot", resumed, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled in Settings", first, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled in Settings", resumed, StringComparison.Ordinal);
        Assert.EndsWith(turn.Question, resumed, StringComparison.Ordinal);
        Assert.DoesNotContain(instructions, resumed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_MCP_configuration_offers_only_the_allowed_tools()
    {
        var config = CopilotAgentProvider.McpConfiguration(new Uri("http://127.0.0.1:5000/mcp"), "token");
        var server = config["mcpServers"]!["dashynms"]!;

        Assert.Equal("http", server["type"]!.ToString());
        Assert.Equal("Bearer token", server["headers"]!["Authorization"]!.ToString());
        Assert.Equal(IntelligenceToolAllowlist.Names.Count, server["tools"]!.AsArray().Count);
    }

    [Fact]
    public void Streams_text_and_takes_the_last_answer_and_session()
    {
        var stream = new CopilotAgentProvider.StreamState(null);
        var listener = new Listener();
        foreach (var line in new[]
        {
            """{"type":"session.start","data":{"sessionId":"session-2"}}""",
            """{"type":"assistant.message_delta","data":{"messageId":"m1","deltaContent":"Looking"}}""",
            """{"type":"assistant.message_delta","data":{"messageId":"m2","deltaContent":"sub","parentToolCallId":"t1"}}""",
            """{"type":"assistant.message","data":{"messageId":"m1","content":"Looking","toolRequests":[{}]}}""",
            """{"type":"tool.execution_start","data":{"toolCallId":"t1","toolName":"dashynms-network_overview","mcpServerName":"dashynms","mcpToolName":"network_overview"}}""",
            """{"type":"assistant.message","data":{"messageId":"m3","content":"All is well."}}""",
            """{"type":"result","sessionId":"session-2","exitCode":0}""",
        })
        {
            Assert.Null(CopilotAgentProvider.Read(JsonNode.Parse(line)!.AsObject(), stream, listener));
        }

        Assert.Equal(["Looking"], listener.Text);
        Assert.Equal("All is well.", stream.Answer);
        Assert.Equal("session-2", stream.Session);
        Assert.True(stream.Finished);
        Assert.Equal(0, stream.ExitCode);
    }

    [Theory]
    [InlineData("""{"type":"tool.execution_start","data":{"toolCallId":"t1","toolName":"shell","arguments":{}}}""")]
    [InlineData("""{"type":"tool.execution_start","data":{"toolCallId":"t1","toolName":"other-tool","mcpServerName":"other"}}""")]
    public void Any_tool_but_DashyNMS_stops_the_answer(string line)
    {
        var stream = new CopilotAgentProvider.StreamState(null);

        var failure = CopilotAgentProvider.Read(JsonNode.Parse(line)!.AsObject(), stream, new Listener());

        Assert.Equal(IntelligenceFailure.Tools, failure!.Failure);
        Assert.NotNull(stream.RefusedTool);
    }

    [Theory]
    [InlineData("""{"type":"session.warning","data":{"warningType":"policy","message":"Third-party MCP servers are disabled by your organization's Copilot policy."}}""")]
    [InlineData("""{"type":"session.warning","data":{"warningType":"mcp","message":"1 MCP server was blocked by policy: 'dashynms' (Could not verify server)"}}""")]
    public void A_policy_that_blocks_the_tools_makes_Copilot_unavailable(string line)
    {
        var failure = CopilotAgentProvider.Read(JsonNode.Parse(line)!.AsObject(), new CopilotAgentProvider.StreamState(null), new Listener());

        Assert.Equal(AgentProviderState.Unavailable, failure!.State);
    }

    [Fact]
    public void Other_warnings_are_ignored()
        => Assert.Null(CopilotAgentProvider.Read(JsonNode.Parse("""{"type":"session.warning","data":{"warningType":"update","message":"A new version is available."}}""")!.AsObject(),
            new CopilotAgentProvider.StreamState(null), new Listener()));

    [Fact]
    public void A_refusal_is_unavailable_and_a_lost_login_is_signed_out()
    {
        Assert.Equal(AgentProviderState.Unavailable, CopilotAgentProvider.Failure("Error: Access denied by policy settings", null, false).State);
        Assert.Equal(AgentProviderState.SignedOut, CopilotAgentProvider.Failure("Please run copilot login", null, false).State);
        Assert.True(CopilotAgentProvider.Failure("Error: session not found", null, resumed: true).SessionLost);
        Assert.False(CopilotAgentProvider.Failure("Error: session not found", null, resumed: false).SessionLost);
        Assert.True(CopilotAgentProvider.Failure("You have exceeded your quota", null, false).Usage!.LimitReached);
    }

    [Fact]
    public void VS_Codes_shim_is_not_the_CLI()
        => Assert.True(CopilotAgentProvider.IsShim(@"C:\Users\someone\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\copilotCli\copilot-shim.ps1"));

    [Fact]
    public async Task Framed_messages_are_read_one_at_a_time()
    {
        var first = """{"id":1,"result":"é"}""";
        var second = """{"id":2}""";
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(first)}\r\nContent-Type: application/json\r\n\r\n{first}Content-Length: {second.Length}\r\n\r\n{second}");
        using var stream = new MemoryStream(bytes);

        Assert.Equal(first, await AgentProcess.ReadFrameAsync(stream));
        Assert.Equal(second, await AgentProcess.ReadFrameAsync(stream));
        Assert.Null(await AgentProcess.ReadFrameAsync(stream));
    }

    [Fact]
    public void A_missing_session_in_the_event_stream_is_lost_only_when_resuming()
    {
        var line = JsonNode.Parse("""{"type":"session.error","data":{"errorType":"session","message":"Session not found"}}""")!.AsObject();

        Assert.True(CopilotAgentProvider.Read(line, new CopilotAgentProvider.StreamState("session-1") { Resumed = true }, new Listener())!.SessionLost);
        Assert.False(CopilotAgentProvider.Read(line, new CopilotAgentProvider.StreamState(null), new Listener())!.SessionLost);
    }

    [Fact]
    public void A_quota_with_a_sliver_left_is_not_used_up()
    {
        var usage = CopilotAgentProvider.Usage(JsonNode.Parse("""
            {"quotaSnapshots":{"chat":{"isUnlimitedEntitlement":false,"entitlementRequests":200,"remainingPercentage":0.04,"usageAllowedWithExhaustedQuota":false,"overageAllowedWithExhaustedQuota":false}}}
            """));

        Assert.Equal(100, usage!.Windows[0].UsedPercent);
        Assert.False(usage.LimitReached);
    }

    private sealed class Listener : IIntelligenceListener
    {
        public List<string> Text { get; } = new();
        public void OnText(string delta) => Text.Add(delta);
        public void OnToolStarted(string name, string arguments) { }
        public void OnToolFinished(string name, bool isError) { }
        public void OnImage(IntelligenceImage image) { }
    }
}

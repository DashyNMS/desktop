using System.Text.Json.Nodes;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Services.Intelligence.Agents;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// The Gemini adapter (#607): its reading of Antigravity CLI's model list and
/// headless stream-json output, and its command line and lockdown, from
/// output shaped as agy 1.5 prints it, with synthetic values.
/// </summary>
public sealed class GeminiAgentProviderTests
{
    private const string ModelList = "Fetching available models...\ngemini-example-high\tGemini Example (High)\ngemini-example-low\tGemini Example (Low)\n";

    [Fact]
    public void Models_come_from_agy_models()
    {
        var models = GeminiAgentProvider.Models(ModelList);

        Assert.Equal(["gemini-example-high", "gemini-example-low"], models.Select(m => m.Id));
        Assert.Equal("Gemini Example (High)", models[0].Name);
        Assert.Empty(models[0].Efforts);
    }

    [Fact]
    public void Listing_models_means_signed_in_with_a_digest_for_its_chats()
    {
        var status = GeminiAgentProvider.Status("agy", "1.5.13", ModelList, "example-project-123");

        Assert.True(status.IsReady);
        Assert.DoesNotContain("example-project", status.AccountKey);
        Assert.NotEqual(status.AccountKey, GeminiAgentProvider.Status("agy", "1.5.13", ModelList, "other-project").AccountKey);
    }

    [Fact]
    public void Asking_to_sign_in_means_signed_out()
        => Assert.Equal(AgentProviderState.SignedOut,
            GeminiAgentProvider.Status("agy", "1", "Fetching available models...\nError: Please sign in to view available models.", null).State);

    [Fact]
    public void The_command_line_keeps_the_question_off_it_and_resumes_a_saved_chat()
    {
        var args = GeminiAgentProvider.Arguments(new AgentTurn("secret question", "gemini-example-high", null, "i", "11111111-2222-3333-4444-555555555555", Temporary: false), "settings.json");

        Assert.DoesNotContain(args, a => a.Contains("secret question"));
        Assert.Equal("stream-json", args[args.IndexOf("--input-format") + 1]);
        Assert.Equal("settings.json", args[args.IndexOf("--settings") + 1]);
        Assert.Equal("11111111-2222-3333-4444-555555555555", args[args.IndexOf("--conversation") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
    }

    [Fact]
    public void A_temporary_chat_never_resumes()
        => Assert.DoesNotContain("--conversation", GeminiAgentProvider.Arguments(new AgentTurn("q", "m", null, "i", "s", Temporary: true), "s.json"));

    [Fact]
    public void The_session_refuses_commands_writes_the_web_the_users_servers_and_the_token_folder()
    {
        var settings = GeminiAgentProvider.SessionSettings(Path.Combine("C:", "work", "run"), ["mine", "dashynms_desktop"]);
        var deny = settings["permissions"]!["deny"]!.AsArray().Select(d => d!.ToString()).ToList();

        Assert.Contains("command(*)", deny);
        Assert.Contains("write_file(*)", deny);
        Assert.Contains("read_url(*)", deny);
        Assert.Contains("execute_url(*)", deny);
        Assert.Contains("mcp(mine/*)", deny);
        Assert.DoesNotContain("mcp(dashynms_desktop/*)", deny);
        Assert.Contains(deny, d => d.StartsWith("read_file(", StringComparison.Ordinal) && d.EndsWith("/.agents/)", StringComparison.Ordinal));
        Assert.Equal("mcp(dashynms_desktop/*)", Assert.Single(settings["permissions"]!["allow"]!.AsArray())!.ToString());
    }

    [Fact]
    public void The_MCP_configuration_loads_only_the_allowed_tools_up_front()
    {
        var server = GeminiAgentProvider.McpConfiguration(new Uri("http://127.0.0.1:5000/mcp"), "token")["mcpServers"]!["dashynms_desktop"]!;

        Assert.Equal("Bearer token", server["headers"]!["Authorization"]!.ToString());
        Assert.Equal(IntelligenceToolAllowlist.Names.Count, server["enabledTools"]!.AsArray().Count);
        Assert.All(server["tools"]!.AsObject(), t => Assert.True(t.Value!["eager"]!.GetValue<bool>()));
    }

    [Fact]
    public void Streams_text_and_takes_the_answer_and_conversation_from_the_result()
    {
        var stream = new GeminiAgentProvider.StreamState(null);
        var listener = new Listener();
        foreach (var line in new[]
        {
            """{"event":"init","conversation_id":"c1","init":{"tools":["call_mcp_tool"]}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c1","step_index":1,"state":"ACTIVE","step_type":"tool","tool_name":"mcp_dashynms_desktop_network_overview"}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c1","step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"All "}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c1","step_index":2,"state":"DONE","step_type":"agent_response","text_delta":"well."}}""",
            """{"event":"result","result":{"conversation_id":"c1","status":"SUCCESS","response":"All well."}}""",
        })
        {
            Assert.Null(GeminiAgentProvider.Read(JsonNode.Parse(line)!.AsObject(), stream, listener));
        }

        Assert.Equal(["All ", "well."], listener.Text);
        Assert.Equal("All well.", stream.Answer);
        Assert.Equal("c1", stream.Session);
        Assert.True(stream.Finished);
    }

    [Fact]
    public void A_conversation_that_has_gone_is_a_lost_session()
    {
        var failure = GeminiAgentProvider.Read(JsonNode.Parse("""{"event":"init","conversation_id":"new"}""")!.AsObject(),
            new GeminiAgentProvider.StreamState("old"), new Listener());

        Assert.True(failure!.SessionLost);
    }

    [Fact]
    public void A_refused_tool_that_fails_lets_the_answer_go_on_but_one_that_gets_through_stops_it()
    {
        var stream = new GeminiAgentProvider.StreamState(null);
        var listener = new Listener();

        Assert.Null(GeminiAgentProvider.Read(Step(3, "ACTIVE", "run_command"), stream, listener));
        Assert.Null(GeminiAgentProvider.Read(Step(3, "ERROR", "run_command"), stream, listener));
        Assert.Equal(IntelligenceFailure.Tools, GeminiAgentProvider.Read(Step(4, "DONE", "list_dir"), stream, listener)!.Failure);
    }

    [Theory]
    [InlineData("invoke_subagent", "subagent")]
    [InlineData("schedule", "tool")]
    [InlineData("mcp_other_server_tool", "tool")]
    [InlineData("search_web", "tool")]
    public void Anything_else_stops_the_answer_as_it_starts(string tool, string type)
    {
        var stream = new GeminiAgentProvider.StreamState(null);

        var failure = GeminiAgentProvider.Read(Step(5, "ACTIVE", tool, type), stream, new Listener());

        Assert.Equal(IntelligenceFailure.Tools, failure!.Failure);
        Assert.Equal(tool, stream.RefusedTool);
    }

    [Fact]
    public void Web_search_is_allowed_and_shown_when_Settings_allows_it()
    {
        var stream = new GeminiAgentProvider.StreamState(null) { WebSearch = true };
        var listener = new Listener();

        Assert.Null(GeminiAgentProvider.Read(Step(6, "ACTIVE", "search_web"), stream, listener));
        Assert.Null(GeminiAgentProvider.Read(Step(6, "DONE", "search_web"), stream, listener));
        Assert.Equal(["started web_search", "finished web_search"], listener.Activity);
    }

    [Fact]
    public void A_call_through_the_general_MCP_tool_is_allowed_only_for_DashyNMS()
    {
        var ours = JsonNode.Parse("""{"event":"step_update","step_update":{"step_index":7,"state":"ACTIVE","step_type":"tool","tool_name":"call_mcp_tool","tool_info":{"name":"call_mcp_tool","parameters":{"ServerName":"dashynms_desktop","ToolName":"network_overview"}}}}""")!.AsObject();
        var theirs = JsonNode.Parse("""{"event":"step_update","step_update":{"step_index":8,"state":"ACTIVE","step_type":"tool","tool_name":"call_mcp_tool","tool_info":{"name":"call_mcp_tool","parameters":{"ServerName":"other","ToolName":"x"}}}}""")!.AsObject();

        Assert.Null(GeminiAgentProvider.Read(ours, new GeminiAgentProvider.StreamState(null), new Listener()));
        Assert.NotNull(GeminiAgentProvider.Read(theirs, new GeminiAgentProvider.StreamState(null), new Listener()));
    }

    [Fact]
    public void Failures_are_worded_with_what_to_do()
    {
        Assert.Equal(AgentProviderState.SignedOut, GeminiAgentProvider.Failure("authentication required").State);
        Assert.True(GeminiAgentProvider.Failure("RESOURCE_EXHAUSTED: quota").Usage!.LimitReached);
        Assert.Equal(IntelligenceFailure.Model, GeminiAgentProvider.Failure("error: invalid model selection").Failure);
    }

    [Fact]
    public void The_first_question_carries_the_instructions()
    {
        Assert.StartsWith("<instructions>", GeminiAgentProvider.Prompt(new AgentTurn("q", "m", null, "Be brief.", null, false)));
        Assert.Equal("q", GeminiAgentProvider.Prompt(new AgentTurn("q", "m", null, "Be brief.", "c1", false)));
    }

    private static JsonObject Step(int index, string state, string tool, string type = "tool")
        => new()
        {
            ["event"] = "step_update",
            ["step_update"] = new JsonObject { ["step_index"] = index, ["state"] = state, ["step_type"] = type, ["tool_name"] = tool },
        };

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

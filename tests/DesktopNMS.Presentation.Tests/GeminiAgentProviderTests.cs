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
public sealed class GeminiAgentProviderTests : IDisposable
{
    private const string Conversation = "11111111-2222-3333-4444-555555555555";
    private const string OtherConversation = "99999999-2222-3333-4444-555555555555";
    private readonly string _geminiHome = Path.Combine(Path.GetTempPath(), "gemini-provider-test-" + Guid.NewGuid().ToString("N"));

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
    public void The_session_refuses_commands_writes_the_web_the_users_servers_and_workspace_reads()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "workspace");
        var settings = GeminiAgentProvider.SessionSettings(workspace, ["mine", "dashynms_desktop"], []);
        var deny = settings["permissions"]!["deny"]!.AsArray().Select(d => d!.ToString()).ToList();

        Assert.Contains("command(*)", deny);
        Assert.Contains("write_file(*)", deny);
        Assert.Contains("read_url(*)", deny);
        Assert.Contains("execute_url(*)", deny);
        Assert.Contains("mcp(mine/*)", deny);
        Assert.DoesNotContain("mcp(dashynms_desktop/*)", deny);
        Assert.Contains($"read_file({workspace.Replace('\\', '/')}/)", deny);
        Assert.Contains("read_file(./)", deny);
        Assert.DoesNotContain("read_file(*)", deny);
        Assert.Equal("", settings["agentMode"]!.GetValue<string>());
        Assert.False(settings["allowNonWorkspaceAccess"]!.GetValue<bool>());
        Assert.Equal("request-review", settings["toolPermission"]!.GetValue<string>());
        Assert.Equal("mcp(dashynms_desktop/*)", Assert.Single(settings["permissions"]!["allow"]!.AsArray())!.ToString());
    }

    [Fact]
    public void Session_denies_override_inherited_file_grants()
    {
        var denials = GeminiAgentProvider.InheritedFileReadDenials(
            """{"permissions":{"allow":["read_file(*)","write_file(/example/)","mcp(other/*)"]}}""");
        Assert.Equal(["read_file(*)", "read_file(/example/)"], denials);

        var settings = GeminiAgentProvider.SessionSettings(Path.GetTempPath(), [], denials);
        var deny = settings["permissions"]!["deny"]!.AsArray().Select(d => d!.ToString());
        Assert.All(denials, rule => Assert.Contains(rule, deny));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"permissions":{"allow":true}}""")]
    public void Unreadable_file_grants_fail_closed(string settings)
        => Assert.Equal(["read_file(*)"], GeminiAgentProvider.InheritedFileReadDenials(settings));

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
        Assert.Equal(IntelligenceFailure.Tools, GeminiAgentProvider.Read(Step(4, "DONE", "read_url_content"), stream, listener)!.Failure);
    }

    [Theory]
    [InlineData("1.5.16", false)]
    [InlineData("1.5.17", true)]
    [InlineData("1.6", true)]
    [InlineData("unknown", false)]
    public void The_hook_requires_a_supported_cli(string version, bool supported)
        => Assert.Equal(supported, GeminiOutputGuard.IsSupportedVersion(version));

    [Fact]
    public void The_hook_launches_only_a_qualified_app_host()
    {
        Assert.Null(GeminiOutputGuard.CreateHook(null));
        Assert.Null(GeminiOutputGuard.CreateHook("DashyNMS"));
        Assert.Null(GeminiOutputGuard.CreateHook(Path.Combine(_geminiHome, "dotnet")));

        var executable = Path.Combine(_geminiHome, "O'Brien's DashyNMS", OperatingSystem.IsWindows() ? "DashyNMS.exe" : "DashyNMS");
        var hook = GeminiOutputGuard.CreateHook(executable)!;
        var command = hook["dashynms-output-read"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.EndsWith(" " + GeminiOutputGuard.HookArgument, command, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith($"\"{executable}\"", command, StringComparison.Ordinal);
            Assert.Null(GeminiOutputGuard.CreateHook(Path.Combine(_geminiHome, "%unsafe%", "DashyNMS.exe")));
        }
        else
        {
            Assert.Contains("O'\"'\"'Brien", command, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_pre_tool_hook_denies_other_reads_before_execution()
    {
        var output = CreateOutput(Conversation);
        var request = new JsonObject
        {
            ["conversationId"] = Conversation,
            ["toolCall"] = new JsonObject
            {
                ["name"] = "read_file",
                ["args"] = new JsonObject { ["Path"] = output },
            },
        };

        Assert.Equal("allow", Decision());
        request["toolCall"]!["args"]!["Path"] = CreateOutput(OtherConversation);
        Assert.Equal("deny", Decision());
        request["toolCall"]!["args"]!["Path"] = output;
        request["toolCall"]!["name"] = "grep_search";
        Assert.Equal("deny", Decision());
        request["toolCall"]!["name"] = "view_file";
        Assert.Equal("deny", Decision());
        Assert.Contains("deny", GeminiOutputGuard.HookDecision("{", _geminiHome), StringComparison.Ordinal);

        string Decision() => JsonNode.Parse(GeminiOutputGuard.HookDecision(request.ToJsonString(), _geminiHome))!["decision"]!.ToString();
    }

    [Fact]
    public void Gemini_can_read_numbered_tool_output_in_the_active_conversation()
    {
        var file = CreateOutput(Conversation);
        var stream = new GeminiAgentProvider.StreamState(Conversation, _geminiHome);

        Assert.Null(GeminiAgentProvider.Read(ReadFile("ACTIVE", Conversation, file), stream, new Listener()));
        Assert.Null(GeminiAgentProvider.Read(ReadFile("DONE", Conversation, file), stream, new Listener()));
    }

    [Fact]
    public void Gemini_accepts_either_case_of_a_Windows_drive_letter()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = CreateOutput(Conversation);
        if (file[1] != ':') return;
        var fromAgent = char.ToLowerInvariant(file[0]) + file[1..];
        var stream = new GeminiAgentProvider.StreamState(Conversation, _geminiHome);

        Assert.Null(GeminiAgentProvider.Read(ReadFile("DONE", Conversation, fromAgent), stream, new Listener()));
    }

    [Fact]
    public void Gemini_stops_reads_with_missing_paths_or_invalid_sessions()
    {
        var file = CreateOutput(Conversation);
        var stream = new GeminiAgentProvider.StreamState(Conversation, _geminiHome);
        Assert.Equal(IntelligenceFailure.Tools, GeminiAgentProvider.Read(ReadFile("DONE", Conversation, null), stream, new Listener())!.Failure);

        var invalid = new GeminiAgentProvider.StreamState("invalid", _geminiHome);
        Assert.Equal(IntelligenceFailure.Tools, GeminiAgentProvider.Read(ReadFile("DONE", "invalid", file), invalid, new Listener())!.Failure);
    }

    [Fact]
    public void Gemini_stops_reads_outside_the_active_conversations_generated_output()
    {
        var file = CreateOutput(Conversation);
        var otherFile = CreateOutput(OtherConversation);
        var directory = Path.GetDirectoryName(file)!;
        var attempts = new[]
        {
            (Conversation, otherFile),
            (OtherConversation, file),
            (Conversation, Path.Combine(directory, "..", "2", "output.txt")),
            (Conversation, Path.Combine(directory, "notes.txt")),
            (Conversation, Path.Combine(_geminiHome, "outside.txt")),
            (Conversation, "output.txt"),
        };
        foreach (var (conversation, path) in attempts)
        {
            var stream = new GeminiAgentProvider.StreamState(Conversation, _geminiHome);
            Assert.Equal(IntelligenceFailure.Tools, GeminiAgentProvider.Read(ReadFile("ACTIVE", conversation, path), stream, new Listener())!.Failure);
            Assert.Equal(IntelligenceFailure.Tools, GeminiAgentProvider.Read(ReadFile("DONE", conversation, path), stream, new Listener())!.Failure);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gemini_refuses_tool_output_reached_through_a_symbolic_link(bool linkDirectory)
    {
        if (OperatingSystem.IsWindows()) return; // Creating a link needs developer mode or administrator access there.
        var file = CreateOutput(Conversation);
        var target = CreateOutput(OtherConversation);
        if (linkDirectory)
        {
            var directory = Path.GetDirectoryName(file)!;
            Directory.Delete(directory, recursive: true);
            Directory.CreateSymbolicLink(directory, Path.GetDirectoryName(target)!);
        }
        else
        {
            File.Delete(file);
            File.CreateSymbolicLink(file, target);
        }
        var stream = new GeminiAgentProvider.StreamState(Conversation, _geminiHome);

        Assert.Equal(IntelligenceFailure.Tools, GeminiAgentProvider.Read(ReadFile("DONE", Conversation, file), stream, new Listener())!.Failure);
    }

    [Theory]
    [InlineData("view_file", "tool")]
    [InlineData("grep_search", "tool")]
    [InlineData("list_dir", "tool")]
    [InlineData("find_by_name", "tool")]
    [InlineData("sed_file", "tool")]
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

    private string CreateOutput(string conversation)
    {
        var directory = Path.Combine(_geminiHome, "antigravity-cli", "brain", conversation, ".system_generated", "steps", "2");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "output.txt");
        File.WriteAllText(output, "Synthetic output");
        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_geminiHome)) Directory.Delete(_geminiHome, recursive: true);
    }

    private static JsonObject ReadFile(string state, string conversation, string? path)
    {
        var update = Step(3, state, "read_file");
        var step = update["step_update"]!.AsObject();
        step["conversation_id"] = conversation;
        step["tool_info"] = new JsonObject { ["parameters"] = new JsonObject { ["Path"] = path } };
        return update;
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

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class IntelligenceWebSearchTests
{
    [Fact]
    public async Task Hosted_search_runs_alongside_network_tools_and_retains_provider_history()
    {
        var handler = new ScriptedHandler(
            Stream(
                """{"type":"response.web_search_call.in_progress"}""",
                """{"type":"response.web_search_call.completed"}""",
                """{"type":"response.output_item.done","item":{"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search","query":"AOS-S release"}}}""",
                """{"type":"response.output_text.delta","delta":"Latest release."}""",
                """{"type":"response.output_item.done","item":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Latest release.","annotations":[{"type":"url_citation","start_index":0,"end_index":15,"url":"https://vendor.example/releases","title":"Release notes"}]}]}}""",
                """{"type":"response.completed"}"""),
            Stream("""{"type":"response.completed"}"""));
        var tools = new NetworkTools();
        var conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient(handler)), tools);
        var listener = new Listener();
        await conversation.AskAsync("Latest AOS-S release?", "test-model", Token, listener, CancellationToken.None, webSearchEnabled: true);
        Assert.Equal("Latest release. [Release notes](https://vendor.example/releases)", listener.Answer);
        Assert.Equal(new[] { "start:web_search", "finish:web_search" }, listener.Activity);
        Assert.Equal(0, tools.Calls);
        await conversation.AskAsync("And the recommended branch?", "test-model", Token, listener, CancellationToken.None, webSearchEnabled: true);
        Assert.Contains(handler.Bodies[0]["tools"]!.AsArray(), t => t?["type"]?.GetValue<string>() == "web_search");
        Assert.Contains(handler.Bodies[0]["tools"]!.AsArray(), t => t?["type"]?.GetValue<string>() == "namespace");
        Assert.Contains(handler.Bodies[1]["input"]!.AsArray(), t => t?["type"]?.GetValue<string>() == "web_search_call");
        Assert.DoesNotContain("[Release notes]", handler.Bodies[1]["input"]!.ToJsonString());
    }

    [Fact]
    public async Task Disabling_search_keeps_network_tools_and_does_not_offer_web_search()
    {
        var handler = new ScriptedHandler(Stream("""{"type":"response.completed"}"""));
        var conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient(handler)), new NetworkTools());
        await conversation.AskAsync("Devices?", "test-model", Token, new Listener(), CancellationToken.None, webSearchEnabled: false);
        Assert.DoesNotContain(handler.Bodies[0]["tools"]!.AsArray(), t => t?["type"]?.GetValue<string>() == "web_search");
        Assert.Contains("Web search is disabled", handler.Bodies[0]["instructions"]!.GetValue<string>());
        var settings = new AppSettings();
        Assert.True(settings.IntelligenceWebSearchEnabled);
        settings.IntelligenceWebSearchEnabled = false;
        Assert.False(settings.Clone().IntelligenceWebSearchEnabled);
    }

    [Fact]
    public async Task Interrupted_stream_is_not_committed_as_a_successful_turn()
    {
        var handler = new ScriptedHandler(Stream("""{"type":"response.output_text.delta","delta":"Partial"}"""));
        var conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient(handler)), new NetworkTools());
        await Assert.ThrowsAsync<IntelligenceException>(() => conversation.AskAsync("Search", "test-model", Token, new Listener(), CancellationToken.None));
        Assert.True(conversation.IsEmpty);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/test.txt")]
    [InlineData("https://user:password@example.org/")]
    public void Unsafe_links_are_not_activated(string value) => Assert.False(WebSearchCitations.TryGetLink(value, out _));

    [Fact]
    public void Provider_markers_become_links_without_removing_the_answer()
    {
        var message = JsonNode.Parse("""{"content":[{"type":"output_text","text":"Release citex.","annotations":[{"type":"url_citation","start_index":8,"end_index":17,"url":"https://example.org/a(b)","title":"Notes"}]}]}""")!.AsObject();
        // Use actual marker offsets rather than relying on its glyph count.
        var part = message["content"]![0]!;
        var text = part["text"]!.GetValue<string>();
        part["annotations"]![0]!["start_index"] = text.IndexOf('\uE200');
        part["annotations"]![0]!["end_index"] = text.IndexOf('\uE201') + 1;
        Assert.Equal("Release [Notes](https://example.org/a%28b%29).", WebSearchCitations.FormatMessage(message));
    }

    [Fact]
    public async Task Tool_exceptions_do_not_send_private_diagnostics_to_the_provider_or_history()
    {
        var handler = new ScriptedHandler(
            Stream(
                """{"type":"response.output_item.done","item":{"type":"function_call","name":"network_overview","call_id":"call_1","arguments":"{}"}}""",
                """{"type":"response.completed"}"""),
            Stream("""{"type":"response.completed"}"""));
        var conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient(handler)), new NetworkTools { ThrowPrivateError = true });
        await conversation.AskAsync("Network health?", "test-model", Token, new Listener(), CancellationToken.None);
        var sent = handler.Bodies[1]["input"]!.ToJsonString();
        Assert.Contains("The network tool failed", sent);
        Assert.DoesNotContain("synthetic-private-token", sent);
        Assert.DoesNotContain("synthetic-private-token", conversation.ExportHistory().ToJsonString());
    }

    [Theory]
    [InlineData(null, "Conversational")]
    [InlineData("conversational", "Conversational")]
    [InlineData("evidence-based", "Evidence based")]
    [InlineData("unknown-style", "Conversational")]
    public async Task Reply_style_is_applied_without_changing_tools_or_evidence_requirements(string? style, string expected)
    {
        var handler = new ScriptedHandler(Stream("""{"type":"response.completed"}"""));
        var conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient(handler)), new NetworkTools());
        await conversation.AskAsync("Network health?", "test-model", Token, new Listener(), CancellationToken.None,
            spotlight: "device test-device", webSearchEnabled: true, replyStyle: style);
        var instructions = handler.Bodies[0]["instructions"]!.GetValue<string>();
        Assert.Contains("Reply style: " + expected, instructions);
        if (expected == "Conversational")
        {
            Assert.Contains("Do not use Markdown tables, section headings or exhaustive inventories unless the user explicitly asks", instructions);
            Assert.Contains("even if earlier answers", instructions);
            Assert.DoesNotContain("Evidence based layout takes precedence", instructions);
        }
        else
        {
            Assert.Contains("one compact bullet per category, metric or finding", instructions);
            Assert.Contains("Category — count · severity", instructions);
            Assert.Contains("for changes use one change per bullet", instructions);
            Assert.Contains("do not silently reuse a previous breakdown", instructions);
            Assert.Contains("a table only when the user requests one", instructions);
        }
        Assert.Contains("retain source citations", instructions);
        Assert.Contains("state important uncertainty", instructions);
        Assert.Contains("Shared uncertainty footer policy for both reply styles", instructions);
        Assert.Contains("beginning [[update]]", instructions);
        Assert.Contains("When no material uncertainty remains, omit the footer entirely", instructions);
        Assert.Contains("[[device:123]]", instructions);
        Assert.Contains("[[alert:456]]", instructions);
        Assert.Contains("do not append a separate status section", instructions);
        Assert.Contains("[[bullet:critical]]", instructions);
        Assert.Contains("[[recommendation]]", instructions);
        Assert.Contains("never include private device names", instructions);
        Assert.Contains("device test-device", instructions);
        Assert.Contains(handler.Bodies[0]["tools"]!.AsArray(), tool => tool?["type"]?.GetValue<string>() == "web_search");
        Assert.Contains(handler.Bodies[0]["tools"]!.AsArray(), tool => tool?["type"]?.GetValue<string>() == "namespace");
    }

    [Fact]
    public void Status_indicator_colour_preference_defaults_on_and_survives_save_and_clone()
    {
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}")!.IntelligenceColourStatusIndicators);
        var settings = new AppSettings { IntelligenceColourStatusIndicators = false };
        Assert.False(settings.Clone().IntelligenceColourStatusIndicators);
        Assert.False(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.IntelligenceColourStatusIndicators);
    }

    [Fact]
    public async Task Switching_reply_style_keeps_the_same_conversations_history()
    {
        var handler = new ScriptedHandler(Stream("""{"type":"response.completed"}"""), Stream("""{"type":"response.completed"}"""));
        var conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient(handler)), new NetworkTools());
        await conversation.AskAsync("First question", "test-model", Token, new Listener(), CancellationToken.None);
        await conversation.AskAsync("Follow-up", "test-model", Token, new Listener(), CancellationToken.None,
            replyStyle: IntelligenceReplyStyles.EvidenceBased);
        Assert.Contains("Reply style: Evidence based", handler.Bodies[1]["instructions"]!.GetValue<string>());
        Assert.Contains("First question", handler.Bodies[1]["input"]!.ToJsonString());
        Assert.Contains("Follow-up", handler.Bodies[1]["input"]!.ToJsonString());
    }

    [Theory]
    [InlineData(IntelligenceReplyStyles.Conversational)]
    [InlineData(IntelligenceReplyStyles.EvidenceBased)]
    public async Task Both_styles_make_recommendations_conditional_on_user_intent_or_a_required_follow_up(string style)
    {
        var handler = new ScriptedHandler(Stream("""{"type":"response.completed"}"""));
        var conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient(handler)), new NetworkTools());
        await conversation.AskAsync("Compare these configs", "test-model", Token, new Listener(), CancellationToken.None, replyStyle: style);
        var instructions = handler.Bodies[0]["instructions"]!.GetValue<string>();
        Assert.Contains("recommendations are conditional, never a default ending", instructions);
        Assert.Contains("user asks for advice, diagnosis or troubleshooting", instructions);
        Assert.Contains("definite, necessary follow-up task", instructions);
        Assert.Contains("config comparison or explanation normally needs facts only", instructions);
        Assert.Contains("omit the recommendation marker and paragraph entirely", instructions);
        Assert.DoesNotContain("then a grounded recommendation", instructions);
        Assert.DoesNotContain("then a brief [[recommendation]] paragraph", instructions);
    }

    [Fact]
    public void Reply_style_defaults_for_older_settings_and_survives_clone_and_serialization()
    {
        Assert.Equal(IntelligenceReplyStyles.Conversational, JsonSerializer.Deserialize<AppSettings>("{}")!.IntelligenceReplyStyle);
        var settings = new AppSettings { IntelligenceReplyStyle = IntelligenceReplyStyles.EvidenceBased };
        Assert.Equal(IntelligenceReplyStyles.EvidenceBased, settings.Clone().IntelligenceReplyStyle);
        Assert.Equal(IntelligenceReplyStyles.EvidenceBased,
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.IntelligenceReplyStyle);
    }

    [Fact]
    public async Task Successful_tool_results_deliver_typed_inline_status_without_an_extra_provider_request()
    {
        var handler = new ScriptedHandler(
            Stream(
                """{"type":"response.output_item.done","item":{"type":"function_call","name":"network_overview","call_id":"call_1","arguments":"{}"}}""",
                """{"type":"response.completed"}"""),
            Stream("""{"type":"response.completed"}"""));
        var tools = new NetworkTools { Output = """{"status":"ok","summary":"8 devices up and 2 down.","data":{"devices":{"up":8,"down":2}}}""" };
        var listener = new Listener();
        var conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient(handler)), tools);
        await conversation.AskAsync("Network status?", "test-model", Token, listener, CancellationToken.None);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("Device status", Assert.Single(listener.StatusCards).Title);
        Assert.Equal("Down", listener.StatusCards[0].Rows[1].Status);
        Assert.Equal("8 devices up and 2 down.", Assert.Single(listener.ToolObservations).Summary);
    }

    private static Task<string> Token(CancellationToken _) => Task.FromResult("test-token");
    private static string Stream(params string[] events) => string.Join("", events.Select(e => "data: " + e + "\n\n"));

    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Bodies.Count - 1], Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class NetworkTools : IIntelligenceTools
    {
        public int Calls { get; private set; }
        public bool ThrowPrivateError { get; init; }
        public string Output { get; init; } = "ok";
        public Task<IReadOnlyList<IntelligenceTool>> GetToolsAsync(CancellationToken _) => Task.FromResult<IReadOnlyList<IntelligenceTool>>(
            new[] { new IntelligenceTool("network_overview", "Read the network", JsonSerializer.SerializeToElement(new { type = "object" })) });
        public Task<IntelligenceToolResult> CallAsync(string name, JsonElement arguments, CancellationToken _)
        {
            Calls++;
            if (ThrowPrivateError) throw new InvalidOperationException("Request failed: synthetic-private-token");
            return Task.FromResult(new IntelligenceToolResult(Output, false));
        }
    }

    private sealed class Listener : IIntelligenceListener
    {
        public string? Answer { get; private set; }
        public List<string> Activity { get; } = new();
        public List<IntelligenceStatusCard> StatusCards { get; } = new();
        public List<IntelligenceToolObservation> ToolObservations { get; } = new();
        public void OnToolObserved(IntelligenceToolObservation observation) => ToolObservations.Add(observation);
        public void OnStatusCard(IntelligenceStatusCard card) => StatusCards.Add(card);
        public void OnText(string text) { }
        public void OnAnswerCompleted(string text) => Answer = text;
        public void OnToolStarted(string name, string arguments) => Activity.Add("start:" + name);
        public void OnToolFinished(string name, bool isError) => Activity.Add("finish:" + name);
        public void OnImage(IntelligenceImage image) { }
    }
}

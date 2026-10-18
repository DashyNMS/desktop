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
        public Task<IReadOnlyList<IntelligenceTool>> GetToolsAsync(CancellationToken _) => Task.FromResult<IReadOnlyList<IntelligenceTool>>(
            new[] { new IntelligenceTool("network_overview", "Read the network", JsonSerializer.SerializeToElement(new { type = "object" })) });
        public Task<IntelligenceToolResult> CallAsync(string name, JsonElement arguments, CancellationToken _)
        {
            Calls++;
            if (ThrowPrivateError) throw new InvalidOperationException("Request failed: synthetic-private-token");
            return Task.FromResult(new IntelligenceToolResult("ok", false));
        }
    }

    private sealed class Listener : IIntelligenceListener
    {
        public string? Answer { get; private set; }
        public List<string> Activity { get; } = new();
        public void OnText(string text) { }
        public void OnAnswerCompleted(string text) => Answer = text;
        public void OnToolStarted(string name, string arguments) => Activity.Add("start:" + name);
        public void OnToolFinished(string name, bool isError) => Activity.Add("finish:" + name);
        public void OnImage(IntelligenceImage image) { }
    }
}

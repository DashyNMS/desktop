using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class SavedIntelligenceChatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dashy-chat-tests-" + Guid.NewGuid().ToString("N"));
    private FileIntelligenceChatStore Store() => new(_root, Transform, Transform);
    private static byte[] Transform(byte[] data) => data.Select(value => (byte)(value ^ 0x5A)).ToArray();

    [Fact]
    public void Reopening_restores_chats_selection_model_context_draft_graphs_and_outfit()
    {
        var store = Store();
        var chat = new SavedIntelligenceChat
        {
            Title = "Core switch", IsRenamed = true, Model = "test-model", Outfit = DashyOutfit.Wizard, ReplyStyle = IntelligenceReplyStyles.EvidenceBased,
            Draft = "What about the ports?",
            History = JsonNode.Parse("""[{"type":"reasoning","encrypted_content":"opaque"},{"type":"function_call","call_id":"1","name":"network_overview","arguments":"{}"},{"type":"function_call_output","call_id":"1","output":"Device health"}]""")!.AsArray(),
            Messages = new() { new() { IsUser = false, Text = "Healthy", Activity = "Looked at device health", ReplyStyle = IntelligenceReplyStyles.Conversational, Images = new() { new(new byte[] { 1, 2, 3 }, "image/png") } } },
            Spotlight = new() { new(0, 12, "core-switch") }, LookedAt = new() { "device health" },
        };
        var second = new SavedIntelligenceChat { Title = "Different chat", Outfit = DashyOutfit.Bowtie };
        chat.Messages[0].StatusCards.Add(new("Device status", "Test scope", DateTimeOffset.UtcNow,
            new[] { new IntelligenceStatusRow("core-switch", "Test location", "Up", "up", DeviceId: 12) }));
        chat.Messages[0].PriorReferences.Add(new("device:99", "earlier-router", "", "unknown", DateTimeOffset.UtcNow, "Earlier lookup"));
        chat.Messages[0].CreatedAt = new DateTimeOffset(2026, 10, 18, 14, 32, 0, TimeSpan.Zero);
        chat.Messages[0].Outcome = IntelligenceReplyOutcome.Failed;
        chat.Messages[0].SubmittedQuestion = "How is the switch?";
        chat.Messages[0].FailureText = "Synthetic lookup failure";
        chat.Messages[0].ToolObservations.Add(new("device_health", chat.Messages[0].CreatedAt.GetValueOrDefault(), "Device is up."));
        store.Save("account-a\nhttps://nms.example/", chat);
        store.Save("account-a\nhttps://nms.example/", second);
        var loaded = Store().Load("account-a\nhttps://nms.example/");
        Assert.Equal(second.Id, loaded.ActiveId);
        Assert.Equal(2, loaded.Chats.Count);
        var restored = loaded.Chats.Single(item => item.Id == chat.Id);
        Assert.Equal(chat.Title, restored.Title);
        Assert.Equal(chat.Draft, restored.Draft);
        Assert.Equal(chat.Model, restored.Model);
        Assert.Equal(IntelligenceReplyStyles.EvidenceBased, restored.ReplyStyle);
        Assert.Equal(chat.Messages[0].CreatedAt, restored.Messages[0].CreatedAt);
        Assert.Equal(IntelligenceReplyOutcome.Failed, restored.Messages[0].Outcome);
        Assert.Equal("How is the switch?", restored.Messages[0].SubmittedQuestion);
        Assert.Equal("Synthetic lookup failure", restored.Messages[0].FailureText);
        Assert.Equal("Device is up.", Assert.Single(restored.Messages[0].ToolObservations).Summary);
        Assert.Equal(DashyOutfit.Wizard, restored.Outfit);
        Assert.Equal(chat.History.ToJsonString(), restored.History.ToJsonString());
        Assert.Equal(chat.Messages[0].Images[0].Data, restored.Messages[0].Images[0].Data);
        Assert.Equal(IntelligenceReplyStyles.Conversational, restored.Messages[0].ReplyStyle);
        var card = Assert.Single(restored.Messages[0].StatusCards);
        Assert.Equal("Up", Assert.Single(card.Rows).Status);
        Assert.Equal(chat.Messages[0].StatusCards[0].ObservedAt, card.ObservedAt);
        Assert.Equal("earlier-router", Assert.Single(restored.Messages[0].PriorReferences).Name);
        Assert.Equal(chat.Spotlight, restored.Spotlight);
        Assert.Equal(chat.LookedAt, restored.LookedAt);
        Assert.DoesNotContain("core-switch", string.Join("", Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Fact]
    public void Accounts_and_servers_have_separate_archives()
    {
        var store = Store();
        store.Save("account-a\nhttps://one.example/", new() { Title = "Private chat" });
        Assert.Empty(store.Load("account-b\nhttps://one.example/").Chats);
        Assert.Empty(store.Load("account-a\nhttps://two.example/").Chats);
        Assert.Single(store.Load("account-a\nhttps://one.example/").Chats);
    }

    [Fact]
    public void Rename_and_delete_only_affect_the_selected_chat()
    {
        var store = Store();
        var one = new SavedIntelligenceChat();
        var two = new SavedIntelligenceChat { Title = "Keep this" };
        store.Save("scope", one);
        store.Save("scope", two);
        one.Title = "Renamed";
        one.IsRenamed = true;
        store.Save("scope", one);
        Assert.Equal("Renamed", store.Load("scope").Chats.Single(chat => chat.Id == one.Id).Title);
        store.Delete("scope", one.Id);
        Assert.Equal(two.Id, Assert.Single(store.Load("scope").Chats).Id);
    }

    [Fact]
    public void Failed_replacement_does_not_destroy_the_last_good_chat()
    {
        var store = Store();
        var chat = new SavedIntelligenceChat { Title = "Original" };
        store.Save("scope", chat);
        chat.Title = "Changed";
        var failing = new FileIntelligenceChatStore(_root, _ => throw new IOException("Disk failure"), Transform);
        Assert.Throws<IOException>(() => failing.Save("scope", chat));
        Assert.Equal("Original", Assert.Single(store.Load("scope").Chats).Title);
    }

    [Fact]
    public void Pinning_another_chat_sorts_it_first_without_changing_the_active_chat()
    {
        var store = Store();
        var pinned = new SavedIntelligenceChat { Title = "Pinned", UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var active = new SavedIntelligenceChat { Title = "Active" };
        store.Save("scope", pinned);
        store.Save("scope", active);
        pinned.IsPinned = true;
        store.Save("scope", pinned, makeActive: false);
        var restored = store.Load("scope");
        Assert.Equal(pinned.Id, restored.Chats[0].Id);
        Assert.Equal(active.Id, restored.ActiveId);
    }

    [Fact]
    public void Conversation_snapshots_are_detached_and_keep_model_context()
    {
        var conversation = new IntelligenceConversation(new ResponsesClient(new HttpClient()), new EmptyTools());
        var history = JsonNode.Parse("""[{"type":"message","role":"user","content":[{"type":"input_text","text":"Question"}]}]""")!.AsArray();
        conversation.RestoreHistory(history);
        history.Clear();
        Assert.False(conversation.IsEmpty);
        var exported = conversation.ExportHistory();
        exported.Clear();
        Assert.Single(conversation.ExportHistory());
        conversation.Clear();
        Assert.True(conversation.IsEmpty);
    }

    private sealed class EmptyTools : IIntelligenceTools
    {
        public Task<IReadOnlyList<IntelligenceTool>> GetToolsAsync(CancellationToken _) => Task.FromResult<IReadOnlyList<IntelligenceTool>>(Array.Empty<IntelligenceTool>());
        public Task<IntelligenceToolResult> CallAsync(string name, JsonElement arguments, CancellationToken _) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("Dashy-chat-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}

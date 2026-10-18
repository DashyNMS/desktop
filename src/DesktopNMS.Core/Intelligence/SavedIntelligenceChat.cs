using System.Text.Json.Nodes;

namespace DesktopNMS.Core.Intelligence;

public sealed class SavedIntelligenceChat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New chat";
    public bool IsRenamed { get; set; }
    public bool IsPinned { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Model { get; set; }
    public string? ReplyStyle { get; set; }
    public DashyOutfit Outfit { get; set; }
    public string Draft { get; set; } = string.Empty;
    public JsonArray History { get; set; } = new();
    public List<SavedIntelligenceMessage> Messages { get; set; } = new();
    public List<SavedChatSpotlight> Spotlight { get; set; } = new();
    public List<string> LookedAt { get; set; } = new();
}

public sealed class SavedIntelligenceMessage
{
    public bool IsUser { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? Activity { get; set; }
    public string? ReplyStyle { get; set; }
    public List<IntelligenceStatusCard> StatusCards { get; set; } = new();
    public List<IntelligenceInlineReference> PriorReferences { get; set; } = new();
    public List<IntelligenceToolObservation> ToolObservations { get; set; } = new();
    public bool IsIncomplete { get; set; }
    public List<IntelligenceImage> Images { get; set; } = new();
}

public sealed record SavedChatSpotlight(int Kind, int Id, string Name);
public sealed record SavedChatCollection(Guid? ActiveId, IReadOnlyList<SavedIntelligenceChat> Chats);

public interface IIntelligenceChatStore
{
    SavedChatCollection Load(string scope);
    void Save(string scope, SavedIntelligenceChat chat, bool makeActive = true);
    void Delete(string scope, Guid id);
}

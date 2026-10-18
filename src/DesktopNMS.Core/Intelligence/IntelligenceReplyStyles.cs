namespace DesktopNMS.Core.Intelligence;

/// <summary>Presentation preferences shared by the desktop settings and agent.</summary>
public static class IntelligenceReplyStyles
{
    public const string Conversational = "conversational";
    public const string EvidenceBased = "evidence-based";

    public static string Normalize(string? style) => style == EvidenceBased ? EvidenceBased : Conversational;
}

using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Intelligence;

/// <summary>Display-only markers: no arbitrary UI, navigation or tool invocation.</summary>
public static partial class IntelligenceInlineMarkup
{
    [GeneratedRegex(@"\[\[(device|alert|status|severity|bullet):([a-z]+|[1-9][0-9]{0,9})\]\]|\[\[(?:recommendation|update)\]\]", RegexOptions.CultureInvariant)]
    public static partial Regex Token();

    [GeneratedRegex(@"```[\s\S]*?(?:```|\z)|`[^`]*`|\[\[(?:device|alert|status|severity|bullet):(?:[a-z]+|[1-9][0-9]{0,9})\]\]|\[\[(?:recommendation|update)\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownToken();

    [GeneratedRegex(@"^\[\[bullet:(critical|warning|ok|unknown)\]\]\s*", RegexOptions.CultureInvariant)]
    public static partial Regex BulletPrefix();

    public static IntelligenceInlineReference? Resolve(string marker, IReadOnlyList<IntelligenceInlineReference> references)
    {
        var match = Token().Match(marker);
        if (!match.Success || match.Length != marker.Length) return null;
        if (marker == "[[recommendation]]") return new("recommendation", "Recommendation", "", "unknown", default, "Suggested next check");
        if (marker == "[[update]]") return new("update", "Notes", "", "unknown", default, "Evidence limitation");
        var kind = match.Groups[1].Value;
        var value = match.Groups[2].Value;
        if (kind is "device" or "alert")
            return references.FirstOrDefault(reference => reference.Key == kind + ":" + value);
        (string Label, string Tone)? label = (kind, value) switch
        {
            ("status", "up") => ("Up", "up"),
            ("status", "down") => ("Down", "down"),
            ("status", "disabled") => ("Disabled", "unknown"),
            ("status", "unknown") => ("Unknown", "unknown"),
            ("severity", "critical") => ("Critical", "critical"),
            ("severity", "warning") => ("Warning", "warning"),
            ("severity", "ok") => ("OK", "up"),
            ("bullet", "critical") => ("Critical", "critical"),
            ("bullet", "warning") => ("Warning", "warning"),
            ("bullet", "ok") => ("OK", "up"),
            ("bullet", "unknown") => ("Unknown", "unknown"),
            _ => null,
        };
        return label is { } known ? new(kind + ":" + value, known.Label, "", known.Tone, default, "Status label") : null;
    }

    public static string Fallback(string marker)
    {
        var match = Token().Match(marker);
        if (marker == "[[update]]") return "Notes";
        return marker == "[[recommendation]]" ? "Recommendation" : match.Success ? char.ToUpperInvariant(match.Groups[1].Value[0]) + match.Groups[1].Value[1..] + " " + match.Groups[2].Value : marker;
    }

    public static string ToReadableMarkdown(string markdown, IReadOnlyList<IntelligenceInlineReference> references)
        => Expand(markdown, references, escapeMarkdown: true);

    public static string ToReadableText(string markdown, IReadOnlyList<IntelligenceInlineReference> references)
        => Expand(markdown, references, escapeMarkdown: false);

    private static string Expand(string markdown, IReadOnlyList<IntelligenceInlineReference> references, bool escapeMarkdown)
        => MarkdownToken().Replace(markdown, match => match.Value.StartsWith('`') ? match.Value
            : match.Value == "[[recommendation]]" ? escapeMarkdown ? "**Recommendation:** " : "Recommendation: "
            : match.Value == "[[update]]" ? escapeMarkdown ? "**Notes:** " : "Notes: "
            : BulletPrefix().IsMatch(match.Value) ? ""
            : Resolve(match.Value, references) is { } reference
                ? (escapeMarkdown ? Escape(reference.Name) : reference.Name) + (reference.Status.Length > 0 ? " (" + reference.Status + ")" : "")
                : Fallback(match.Value));

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_")
        .Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");
}

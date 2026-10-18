using System.Text.Json.Nodes;

namespace DesktopNMS.Core.Intelligence;

/// <summary>Turns provider citation annotations into visible, clickable Markdown sources.</summary>
public static class WebSearchCitations
{
    public static bool TryGetLink(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "https" or "http" && string.IsNullOrEmpty(parsed.UserInfo))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    public static string FormatMessage(JsonObject message)
    {
        if (message["content"] is not JsonArray content) return string.Empty;
        return string.Join("\n\n", content.OfType<JsonObject>()
            .Where(part => part["type"]?.GetValue<string>() == "output_text")
            .Select(FormatPart));
    }

    private static string FormatPart(JsonObject part)
    {
        var text = part["text"]?.GetValue<string>() ?? string.Empty;
        if (part["annotations"] is not JsonArray annotations) return text;
        var citations = annotations.OfType<JsonObject>()
            .Where(a => a["type"]?.GetValue<string>() == "url_citation")
            .Select(a => new
            {
                Url = a["url"]?.GetValue<string>(),
                Title = a["title"]?.GetValue<string>(),
                Start = a["start_index"] is JsonValue start && start.TryGetValue<int>(out var s) ? s : -1,
                End = a["end_index"] is JsonValue end && end.TryGetValue<int>(out var e) ? e : -1,
            })
            .Where(a => a.Start >= 0 && a.End >= a.Start && a.End <= text.Length)
            .OrderByDescending(a => a.Start).Take(60);
        var boundary = text.Length;
        foreach (var citation in citations)
        {
            if (citation.End > boundary || !TryGetLink(citation.Url, out var uri)) continue;
            var label = string.IsNullOrWhiteSpace(citation.Title) ? uri.Host : citation.Title;
            label = label.Replace('[', '(').Replace(']', ')').Replace('\r', ' ').Replace('\n', ' ').Replace('\\', ' ');
            if (label.Length > 160) label = label[..160];
            var url = uri.AbsoluteUri.Replace("(", "%28").Replace(")", "%29");
            var link = $" [{label}]({url})";
            var span = text[citation.Start..citation.End];
            // Some providers annotate prose; preserve it. Citation-marker
            // spans can be replaced without leaving unreadable control glyphs.
            text = span.Contains('\uE200') || span.Contains('\uE202')
                ? text[..citation.Start] + link.TrimStart() + text[citation.End..]
                : text.Insert(citation.End, link);
            boundary = citation.Start;
        }
        return text;
    }
}

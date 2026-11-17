namespace DesktopNMS.Services.Intelligence;

/// <summary>
/// A fenced code block's opening line, as CommonMark reads it: three or more
/// backticks or tildes. Only a line of the same character, at least as long and
/// with nothing after it, closes it, so a shorter fence, the other character or
/// a fence with a language after it stays inside the code.
/// </summary>
public readonly record struct MarkdownFence(char Marker, int Length)
{
    /// <summary>The line that closes this block.</summary>
    public string Closer => new(Marker, Length);

    /// <param name="nested">For display, which has no nested blocks: code indented under a list item still counts.</param>
    public static bool TryOpen(string line, out MarkdownFence fence, bool nested = false)
    {
        fence = default;
        if (Unindented(line, nested) is not { } text) return false;
        if (text.Length < 3 || text[0] is not ('`' or '~')) return false;
        var length = 0;
        while (length < text.Length && text[length] == text[0]) length++;

        // A backtick fence's language can't contain a backtick: that's inline code.
        if (length < 3 || (text[0] == '`' && text.AsSpan(length).Contains('`'))) return false;
        fence = new MarkdownFence(text[0], length);
        return true;
    }

    public bool IsClosedBy(string line, bool nested = false)
    {
        if (Unindented(line, nested) is not { } unindented) return false;
        var text = unindented.TrimEnd();
        var marker = Marker;
        return text.Length >= Length && text.All(c => c == marker);
    }

    /// <summary>The line without its indent, or null when indented four or more spaces (or a tab): CommonMark reads that as code, not a fence.</summary>
    private static string? Unindented(string line, bool nested)
    {
        if (nested) return line.TrimStart();
        var spaces = 0;
        while (spaces < line.Length && line[spaces] == ' ') spaces++;
        return spaces <= 3 && (spaces == line.Length || line[spaces] != '\t') ? line[spaces..] : null;
    }

    /// <summary>The fence still open at the end of <paramref name="markdown"/> - an answer stopped mid-code - or null.</summary>
    /// <param name="nested">Scan as the chat view and PDF do, for text they will show.</param>
    public static MarkdownFence? Unclosed(string markdown, bool nested = false)
    {
        MarkdownFence? open = null;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (open is { } fence)
            {
                if (fence.IsClosedBy(line, nested)) open = null;
            }
            else if (TryOpen(line, out var opened, nested))
            {
                open = opened;
            }
        }

        return open;
    }
}

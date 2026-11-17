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

    public static bool TryOpen(string line, out MarkdownFence fence)
    {
        fence = default;
        var text = line.TrimStart();
        if (text.Length < 3 || text[0] is not ('`' or '~')) return false;
        var length = 0;
        while (length < text.Length && text[length] == text[0]) length++;

        // A backtick fence's language can't contain a backtick: that's inline code.
        if (length < 3 || (text[0] == '`' && text.AsSpan(length).Contains('`'))) return false;
        fence = new MarkdownFence(text[0], length);
        return true;
    }

    public bool IsClosedBy(string line)
    {
        var text = line.Trim();
        var marker = Marker;
        return text.Length >= Length && text.All(c => c == marker);
    }

    /// <summary>The fence still open at the end of <paramref name="markdown"/> - an answer stopped mid-code - or null.</summary>
    public static MarkdownFence? Unclosed(string markdown)
    {
        MarkdownFence? open = null;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (open is { } fence)
            {
                if (fence.IsClosedBy(line)) open = null;
            }
            else if (TryOpen(line, out var opened))
            {
                open = opened;
            }
        }

        return open;
    }
}

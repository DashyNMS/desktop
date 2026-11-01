using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.Services.Intelligence;

[Flags]
public enum ChatTextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
}

/// <summary>A run of text in one style. <see cref="Link"/> is set only for a web address that's safe to open.</summary>
public sealed record ChatTextSpan(string Text, ChatTextStyle Style, Uri? Link = null);

public enum ChatColumnAlignment
{
    Left,
    Centre,
    Right,
}

public abstract record ChatBlock;

public sealed record ChatHeadingBlock(int Level, IReadOnlyList<ChatTextSpan> Spans) : ChatBlock;

public sealed record ChatParagraphBlock(IReadOnlyList<ChatTextSpan> Spans) : ChatBlock;

public sealed record ChatQuoteBlock(IReadOnlyList<ChatTextSpan> Spans) : ChatBlock;

/// <param name="Depth">0 for a top-level item, 1 for one nested under it, and so on.</param>
/// <param name="Marker">"•", or the item's number with its full stop.</param>
public sealed record ChatListItemBlock(int Depth, string Marker, IReadOnlyList<ChatTextSpan> Spans) : ChatBlock;

public sealed record ChatCodeBlock(string Text) : ChatBlock;

/// <param name="Rows">The header row first. Each cell is its own spans.</param>
public sealed record ChatTableBlock(IReadOnlyList<IReadOnlyList<IReadOnlyList<ChatTextSpan>>> Rows, IReadOnlyList<ChatColumnAlignment> Alignments) : ChatBlock;

public sealed record ChatRuleBlock : ChatBlock;

/// <summary>
/// Reads an answer's Markdown into blocks for the PDF export (#315). It covers
/// what the chat itself shows (see MarkdownView): headings, paragraphs, lists,
/// tables, fenced code, quotes, rules, and bold, italic, code and links within
/// a line. It expects the readable Markdown, where device and alert references
/// have already become names.
/// </summary>
public static partial class ChatMarkdownBlocks
{
    public static IReadOnlyList<ChatBlock> Parse(string markdown)
    {
        var blocks = new List<ChatBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                    code.Add(lines[i++]);
                i++;
                blocks.Add(new ChatCodeBlock(string.Join('\n', code)));
                continue;
            }

            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                blocks.Add(new ChatHeadingBlock(heading.Groups[1].Length, Inline(heading.Groups[2].Value)));
                i++;
                continue;
            }

            if (RuleLine().IsMatch(trimmed))
            {
                blocks.Add(new ChatRuleBlock());
                i++;
                continue;
            }

            if (IsTableStart(lines, i))
            {
                var alignments = Cells(lines[i + 1]).Select(Alignment).ToList();
                var rows = new List<IReadOnlyList<IReadOnlyList<ChatTextSpan>>> { Cells(trimmed).Select(cell => Inline(cell)).ToList() };
                i += 2;
                while (i < lines.Length && lines[i].Trim().StartsWith('|'))
                    rows.Add(Cells(lines[i++]).Select(cell => Inline(cell)).ToList());
                blocks.Add(new ChatTableBlock(rows, alignments));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].Trim().StartsWith('>'))
                    quote.Append(lines[i++].Trim().TrimStart('>').Trim()).Append(' ');
                blocks.Add(new ChatQuoteBlock(Inline(quote.ToString().Trim())));
                continue;
            }

            if (ListItem().IsMatch(line))
            {
                ReadList(lines, ref i, blocks);
                continue;
            }

            var prose = new StringBuilder(trimmed);
            i++;
            while (i < lines.Length && lines[i].Trim().Length > 0 && !StartsBlock(lines, i))
                prose.Append(' ').Append(lines[i++].Trim());
            blocks.Add(new ChatParagraphBlock(Inline(prose.ToString())));
        }

        return blocks;
    }

    private static void ReadList(string[] lines, ref int i, List<ChatBlock> blocks)
    {
        // Indents seen so far, outermost first; an item's depth is where its indent fits.
        var indents = new List<int>();
        var numbers = new Dictionary<int, int>();
        while (i < lines.Length && ListItem().Match(lines[i]) is { Success: true } item)
        {
            var indent = item.Groups[1].Value.Length;
            while (indents.Count > 0 && indent < indents[^1] - 1) indents.RemoveAt(indents.Count - 1);
            if (indents.Count == 0 || indent > indents[^1] + 1) indents.Add(indent);
            var depth = Math.Min(indents.Count - 1, 4);
            foreach (var deeper in numbers.Keys.Where(key => key > depth).ToList()) numbers.Remove(deeper);

            var text = new StringBuilder(item.Groups[3].Value.Trim());
            i++;
            while (i < lines.Length && lines[i].Trim().Length > 0 && !ListItem().IsMatch(lines[i]) && !StartsBlock(lines, i) && lines[i].StartsWith(' '))
                text.Append(' ').Append(lines[i++].Trim());

            var numbered = char.IsDigit(item.Groups[2].Value[0]);
            var marker = "•";
            if (numbered)
            {
                numbers[depth] = numbers.GetValueOrDefault(depth) + 1;
                marker = numbers[depth] + ".";
            }

            blocks.Add(new ChatListItemBlock(depth, marker, Inline(text.ToString())));
        }
    }

    /// <summary>Bold, italic, inline code and links within a line, with Markdown's backslash escapes undone.</summary>
    public static IReadOnlyList<ChatTextSpan> Inline(string text) => Merge(Inline(text, ChatTextStyle.None, null));

    private static IEnumerable<ChatTextSpan> Inline(string text, ChatTextStyle style, Uri? link)
    {
        var position = 0;
        foreach (Match match in InlineToken().Matches(text))
        {
            if (match.Index > position) yield return new(text[position..match.Index], style, link);
            var token = match.Value;
            if (token[0] == '\\')
            {
                yield return new(token[1..], style, link);
            }
            else if (token.StartsWith("**", StringComparison.Ordinal) || token.StartsWith("__", StringComparison.Ordinal))
            {
                foreach (var span in Inline(token[2..^2], style | ChatTextStyle.Bold, link)) yield return span;
            }
            else if (token[0] == '`')
            {
                yield return new(token.Trim('`'), style | ChatTextStyle.Code, link);
            }
            else if (token[0] == '[')
            {
                var parts = LinkToken().Match(token);
                // Only a plain web address becomes clickable; anything else keeps its words.
                var target = WebSearchCitations.TryGetLink(parts.Groups[2].Value, out var uri) ? uri : link;
                foreach (var span in Inline(parts.Groups[1].Value, style, target)) yield return span;
            }
            else
            {
                foreach (var span in Inline(token[1..^1], style | ChatTextStyle.Italic, link)) yield return span;
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length) yield return new(text[position..], style, link);
    }

    private static IReadOnlyList<ChatTextSpan> Merge(IEnumerable<ChatTextSpan> spans)
    {
        var merged = new List<ChatTextSpan>();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Style == span.Style && merged[^1].Link == span.Link)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };
            else
                merged.Add(span);
        }

        return merged;
    }

    private static bool IsTableStart(string[] lines, int i)
        => lines[i].Trim().StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1].Trim());

    private static bool StartsBlock(string[] lines, int i)
    {
        var t = lines[i].Trim();
        return t.StartsWith("```", StringComparison.Ordinal) || Heading().IsMatch(t) || RuleLine().IsMatch(t)
            || t.StartsWith('>') || ListItem().IsMatch(lines[i]) || IsTableStart(lines, i);
    }

    private static List<string> Cells(string row)
    {
        var inner = row.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|') && !inner.EndsWith("\\|", StringComparison.Ordinal)) inner = inner[..^1];
        return CellSplit().Split(inner).Select(cell => cell.Trim()).ToList();
    }

    private static ChatColumnAlignment Alignment(string separator)
    {
        var s = separator.Trim();
        return (s.StartsWith(':'), s.EndsWith(':')) switch
        {
            (true, true) => ChatColumnAlignment.Centre,
            (false, true) => ChatColumnAlignment.Right,
            _ => ChatColumnAlignment.Left,
        };
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();

    // Group 1 indent, 2 marker, 3 text.
    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    // A cell boundary is a pipe that isn't escaped.
    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex CellSplit();

    // An escape first, then bold before italic. Single underscores only at
    // word edges, so hostnames like core_sw_01 stay as they are.
    [GeneratedRegex(@"\\[\\`*_\[\]{}()#+\-.!|<>~]|\*\*(?=\S).+?(?<=\S)\*\*|__(?=\S).+?(?<=\S)__|`+[^`]+`+|\[(?:\\.|[^\]\\])+\]\([^)\s]+\)|(?<![\w*\\])\*(?=\S)[^*]+?(?<=\S)\*(?![\w*])|(?<![\w\\])_(?=\S)[^_]+?(?<=\S)_(?!\w)")]
    private static partial Regex InlineToken();

    [GeneratedRegex(@"^\[((?:\\.|[^\]\\])+)\]\(([^)\s]+)\)$")]
    private static partial Regex LinkToken();
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DesktopNMS.Services.Intelligence;

namespace DesktopNMS.Services.Help;

/// <summary>A run of text in one style. <see cref="Link"/> is a link's target as it's written, for the guide to resolve.</summary>
public sealed record HelpSpan(string Text, bool IsBold = false, bool IsItalic = false, bool IsCode = false, string? Link = null);

public abstract record HelpBlock;

public sealed record HelpHeadingBlock(int Level, IReadOnlyList<HelpSpan> Spans) : HelpBlock
{
    public string Text => string.Concat(Spans.Select(span => span.Text));
}

public sealed record HelpParagraphBlock(IReadOnlyList<HelpSpan> Spans) : HelpBlock;

/// <param name="Start">The first item's number, or null for bullets.</param>
public sealed record HelpListBlock(int? Start, IReadOnlyList<IReadOnlyList<HelpBlock>> Items) : HelpBlock;

public sealed record HelpCodeBlock(string Text) : HelpBlock;

/// <param name="Rows">The header row first.</param>
public sealed record HelpTableBlock(IReadOnlyList<IReadOnlyList<IReadOnlyList<HelpSpan>>> Rows) : HelpBlock;

public sealed record HelpQuoteBlock(IReadOnlyList<HelpBlock> Blocks) : HelpBlock;

public sealed record HelpImageBlock(string AltText, string Target) : HelpBlock;

public sealed record HelpRuleBlock : HelpBlock;

/// <summary>
/// Reads a help page's Markdown into blocks the app draws itself (#604), so a
/// page looks like the rest of DashyNMS rather than like a web page. It covers
/// what the guide uses: headings, paragraphs, lists with anything inside them,
/// tables, fenced code, quotes, images, rules, and bold, italic, code and
/// links within a line. Raw HTML never gets this far: Core leaves it out.
/// </summary>
public static partial class HelpBlocks
{
    /// <summary>Deeper than any real page nests, so a strange one can't run away.</summary>
    private const int MaxDepth = 8;

    public static IReadOnlyList<HelpBlock> Read(string? markdown)
        => Read((markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), 0);

    private static List<HelpBlock> Read(IReadOnlyList<string> lines, int depth)
    {
        var blocks = new List<HelpBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                i++;
            }
            else if (MarkdownFence.TryOpen(line, out var fence, nested: true))
            {
                var code = new List<string>();
                for (i++; i < lines.Count && !fence.IsClosedBy(lines[i], nested: true); i++)
                {
                    code.Add(lines[i]);
                }

                i++;
                blocks.Add(new HelpCodeBlock(string.Join('\n', code)));
            }
            else if (Heading().Match(trimmed) is { Success: true } heading)
            {
                blocks.Add(new HelpHeadingBlock(heading.Groups[1].Length, Spans(heading.Groups[2].Value)));
                i++;
            }
            else if (Rule().IsMatch(trimmed))
            {
                blocks.Add(new HelpRuleBlock());
                i++;
            }
            else if (IsTable(lines, i))
            {
                var rows = new List<IReadOnlyList<IReadOnlyList<HelpSpan>>> { Cells(trimmed) };
                for (i += 2; i < lines.Count && lines[i].Trim().StartsWith('|'); i++)
                {
                    rows.Add(Cells(lines[i].Trim()));
                }

                blocks.Add(new HelpTableBlock(rows));
            }
            else if (trimmed.StartsWith('>') && depth < MaxDepth)
            {
                var quoted = new List<string>();
                for (; i < lines.Count && lines[i].TrimStart().StartsWith('>'); i++)
                {
                    var inner = lines[i].TrimStart()[1..];
                    quoted.Add(inner.StartsWith(' ') ? inner[1..] : inner);
                }

                blocks.Add(new HelpQuoteBlock(Read(quoted, depth + 1)));
            }
            else if (Item().IsMatch(line) && depth < MaxDepth)
            {
                blocks.Add(List(lines, ref i, depth));
            }
            else if (Image().Match(trimmed) is { Success: true } image)
            {
                blocks.Add(new HelpImageBlock(Unescape(image.Groups[1].Value), image.Groups[2].Value));
                i++;
            }
            else
            {
                var prose = new StringBuilder(trimmed);
                for (i++; i < lines.Count && lines[i].Trim().Length > 0 && !StartsBlock(lines, i); i++)
                {
                    prose.Append(' ').Append(lines[i].Trim());
                }

                blocks.Add(new HelpParagraphBlock(Spans(prose.ToString())));
            }
        }

        return blocks;
    }

    /// <summary>One list: its items, each with the lines indented under it, which can be anything.</summary>
    private static HelpListBlock List(IReadOnlyList<string> lines, ref int i, int depth)
    {
        var first = Item().Match(lines[i]);
        var indent = first.Groups[1].Length;
        var numbered = char.IsDigit(first.Groups[2].Value[0]);
        var start = numbered && int.TryParse(first.Groups[2].Value.TrimEnd('.', ')'), out var number) ? number : 1;
        var items = new List<IReadOnlyList<HelpBlock>>();

        while (i < lines.Count && Item().Match(lines[i]) is { Success: true } item
               && item.Groups[1].Length == indent && char.IsDigit(item.Groups[2].Value[0]) == numbered)
        {
            // What's under an item lines up with its text, or is at least indented past its marker.
            var text = item.Groups[3].Index;
            var content = new List<string> { lines[i][text..] };
            i++;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                {
                    // A gap belongs to the item only when something indented follows it.
                    var next = i + 1;
                    while (next < lines.Count && lines[next].Trim().Length == 0)
                    {
                        next++;
                    }

                    if (next >= lines.Count || Indent(lines[next]) <= indent)
                    {
                        break;
                    }

                    content.Add(string.Empty);
                    i++;
                    continue;
                }

                var lineIndent = Indent(line);
                if (lineIndent > indent)
                {
                    content.Add(line[Math.Min(lineIndent, text)..]);
                    i++;
                }
                else if (Item().IsMatch(line) || StartsBlock(lines, i) || content[^1].Length == 0)
                {
                    break;
                }
                else
                {
                    // A line that carries the item's paragraph on without being indented.
                    content.Add(line.Trim());
                    i++;
                }
            }

            items.Add(Read(content, depth + 1));

            // Loose items have a gap between them.
            var gap = i;
            while (gap < lines.Count && lines[gap].Trim().Length == 0)
            {
                gap++;
            }

            if (gap < lines.Count && gap > i && Item().Match(lines[gap]) is { Success: true } following
                && following.Groups[1].Length == indent && char.IsDigit(following.Groups[2].Value[0]) == numbered)
            {
                i = gap;
            }
        }

        return new HelpListBlock(numbered ? start : null, items);
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static bool StartsBlock(IReadOnlyList<string> lines, int i)
    {
        var trimmed = lines[i].Trim();
        return MarkdownFence.TryOpen(lines[i], out _, nested: true)
               || Heading().IsMatch(trimmed)
               || Rule().IsMatch(trimmed)
               || trimmed.StartsWith('>')
               || Item().IsMatch(lines[i])
               || Image().IsMatch(trimmed)
               || IsTable(lines, i);
    }

    private static bool IsTable(IReadOnlyList<string> lines, int i)
        => lines[i].Trim().StartsWith('|') && i + 1 < lines.Count && TableRule().IsMatch(lines[i + 1].Trim());

    private static List<IReadOnlyList<HelpSpan>> Cells(string row)
    {
        var inner = row.Trim();
        inner = inner.StartsWith('|') ? inner[1..] : inner;
        inner = inner.EndsWith('|') && !inner.EndsWith("\\|", StringComparison.Ordinal) ? inner[..^1] : inner;
        return CellBreak().Split(inner).Select(cell => Spans(cell.Trim().Replace("\\|", "|", StringComparison.Ordinal))).ToList();
    }

    /// <summary>A line's runs of text. Links keep their target for the guide to resolve; an image within a line becomes its alt text.</summary>
    internal static List<HelpSpan> Spans(string text)
    {
        var spans = new List<HelpSpan>();
        Add(spans, text, new HelpSpan(string.Empty), 0);
        return spans;
    }

    private static void Add(List<HelpSpan> spans, string text, HelpSpan style, int depth)
    {
        var position = 0;
        foreach (Match match in Inline().Matches(text))
        {
            Plain(text[position..match.Index]);
            position = match.Index + match.Length;
            if (match.Groups["code"].Success)
            {
                spans.Add(style with { Text = match.Groups["code"].Value.Trim(), IsCode = true });
            }
            else if (match.Groups["image"].Success)
            {
                Plain(match.Groups["image"].Value);
            }
            else if (depth >= MaxDepth)
            {
                Plain(match.Value);
            }
            else if (match.Groups["link"].Success)
            {
                Add(spans, match.Groups["link"].Value, style with { Link = match.Groups["target"].Value }, depth + 1);
            }
            else if (match.Groups["bold"].Success)
            {
                Add(spans, match.Groups["bold"].Value, style with { IsBold = true }, depth + 1);
            }
            else if (match.Groups["italic"].Success)
            {
                Add(spans, match.Groups["italic"].Value, style with { IsItalic = true }, depth + 1);
            }
            else if (match.Groups["address"].Success)
            {
                spans.Add(style with { Text = match.Groups["address"].Value, Link = match.Groups["address"].Value });
            }
        }

        Plain(text[position..]);

        void Plain(string part)
        {
            if (part.Length > 0)
            {
                spans.Add(style with { Text = System.Net.WebUtility.HtmlDecode(Unescape(part)) });
            }
        }
    }

    private static string Unescape(string text) => Escape().Replace(text, "$1");

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^( *)([-*+]|\d{1,3}[.)])\s+(\S.*)$")]
    private static partial Regex Item();

    [GeneratedRegex(@"^!\[((?:\\.|[^\]\\])*)\]\(([^)\s]+)\)$")]
    private static partial Regex Image();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex CellBreak();

    [GeneratedRegex(@"(?<!`)(?<tick>`+)(?!`)(?<code>.+?)(?<!`)\k<tick>(?!`)"
        + @"|!\[(?<image>(?:\\.|[^\]\\])*)\]\([^)\s]+\)"
        + @"|(?<!\\)\[(?<link>(?:\\.|[^\]\\])+)\]\((?<target>[^)\s]+)\)"
        + @"|<(?<address>https?://[^>\s]+)>"
        + @"|\*\*(?=\S)(?<bold>.+?)(?<=\S)\*\*"
        + @"|(?<![\w*\\])\*(?=\S)(?<italic>[^*]+?)(?<=\S)\*(?![\w*])"
        + @"|(?<![\w\\])_(?=\S)(?<italic>[^_]+?)(?<=\S)_(?!\w)")]
    private static partial Regex Inline();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|<>~])")]
    private static partial Regex Escape();
}

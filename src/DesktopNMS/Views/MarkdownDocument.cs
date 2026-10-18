using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopNMS.Views;

/// <summary>
/// Shows Markdown - what the Intelligence agent answers in - as a WPF
/// document in a read-only RichTextBox, so it can be selected and copied:
/// <code>&lt;RichTextBox views:MarkdownDocument.Markdown="{Binding Text}" IsReadOnly="True" /&gt;</code>
/// Covers what a chat answer uses: paragraphs, headings, bullet and numbered
/// lists, tables (with column alignment), fenced code, quotes, rules, and
/// bold, italic, inline code and links inline. Links show their text but
/// don't open - their targets can come from network data. While an answer
/// streams in, it's redrawn at most every 120 ms.
/// </summary>
public static partial class MarkdownDocument
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached(
        "Markdown",
        typeof(string),
        typeof(MarkdownDocument),
        new PropertyMetadata(null, OnMarkdownChanged));

    private static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached(
        "Timer",
        typeof(DispatcherTimer),
        typeof(MarkdownDocument),
        new PropertyMetadata(null));

    public static string? GetMarkdown(DependencyObject element) => (string?)element.GetValue(MarkdownProperty);

    public static void SetMarkdown(DependencyObject element, string? value) => element.SetValue(MarkdownProperty, value);

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box)
        {
            return;
        }

        // The first text draws straight away; changes after that are batched.
        if (e.OldValue is null)
        {
            box.Document = new Renderer(box).Render(e.NewValue as string ?? string.Empty);
            return;
        }

        if (box.GetValue(TimerProperty) is not DispatcherTimer timer)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, box.Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                box.Document = new Renderer(box).Render(GetMarkdown(box) ?? string.Empty);
            };
            box.SetValue(TimerProperty, timer);
        }

        if (!timer.IsEnabled)
        {
            timer.Start();
        }
    }

    public static FlowDocument Render(string markdown) => new Renderer(null).Render(markdown);

    // Resolve colours at the answer card first, so Markdown stays readable on
    // its navy surface in both themes, including tables, quotes and code.
    private sealed class Renderer(FrameworkElement? scope)
    {
        public FlowDocument Render(string markdown)
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = Resource<FontFamily>("BodyFont") ?? new FontFamily("Segoe UI"),
                FontSize = 14,
                LineHeight = 21,
                Foreground = Brush("TextPrimaryBrush"),
                TextAlignment = TextAlignment.Left,
            };

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

                // ``` fenced code
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    var code = new StringBuilder();
                    i++;
                    while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                    {
                        if (code.Length > 0)
                        {
                            code.Append('\n');
                        }

                        code.Append(lines[i]);
                        i++;
                    }

                    i++;
                    document.Blocks.Add(CodeBlock(code.ToString()));
                    continue;
                }

                // # Heading
                if (Heading().Match(trimmed) is { Success: true } heading)
                {
                    var level = heading.Groups[1].Value.Length;
                    var paragraph = new Paragraph
                    {
                        FontWeight = FontWeights.SemiBold,
                        FontSize = level switch { 1 => 19, 2 => 17, _ => 15 },
                        Margin = new Thickness(0, 6, 0, 6),
                    };
                    AddInlines(paragraph.Inlines, heading.Groups[2].Value);
                    document.Blocks.Add(paragraph);
                    i++;
                    continue;
                }

                // --- rule
                if (Rule().IsMatch(trimmed))
                {
                    document.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = Brush("BorderBrush"), Margin = new Thickness(0, 6, 0, 10) }));
                    i++;
                    continue;
                }

                // | table |, with a |---| row under its header
                if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1].Trim()))
                {
                    var rows = new List<string> { trimmed };
                    var alignments = Cells(lines[i + 1].Trim()).Select(Alignment).ToList();
                    i += 2;
                    while (i < lines.Length && lines[i].Trim().StartsWith('|'))
                    {
                        rows.Add(lines[i].Trim());
                        i++;
                    }

                    document.Blocks.Add(Table(rows, alignments));
                    continue;
                }

                // > quote
                if (trimmed.StartsWith('>'))
                {
                    var quote = new StringBuilder();
                    while (i < lines.Length && lines[i].Trim().StartsWith('>'))
                    {
                        quote.Append(lines[i].Trim().TrimStart('>').Trim()).Append(' ');
                        i++;
                    }

                    var paragraph = new Paragraph { Foreground = Brush("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(10, 0, 0, 0), BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(3, 0, 0, 0) };
                    AddInlines(paragraph.Inlines, quote.ToString().Trim());
                    document.Blocks.Add(paragraph);
                    continue;
                }

                // - bullets / 1. numbers
                if (ListItem().IsMatch(line))
                {
                    document.Blocks.Add(ListBlock(lines, ref i));
                    continue;
                }

                // A paragraph: lines up to the next blank line or block.
                var text = new StringBuilder(trimmed);
                i++;
                while (i < lines.Length && lines[i].Trim().Length > 0 && !StartsBlock(lines, i))
                {
                    text.Append(' ').Append(lines[i].Trim());
                    i++;
                }

                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                AddInlines(p.Inlines, text.ToString());
                document.Blocks.Add(p);
            }

            // No space under the last block.
            if (document.Blocks.LastBlock is { } last)
            {
                last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
            }

            return document;
        }

        private bool StartsBlock(string[] lines, int i)
        {
            var t = lines[i].Trim();
            return t.StartsWith("```", StringComparison.Ordinal)
                   || Heading().IsMatch(t)
                   || Rule().IsMatch(t)
                   || t.StartsWith('>')
                   || ListItem().IsMatch(lines[i])
                   || (t.StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1].Trim()));
        }

        /// <summary>A run of list items - bullets or numbers - with one level of nesting by indent.</summary>
        private List ListBlock(string[] lines, ref int i)
        {
            var first = ListItem().Match(lines[i]);
            var baseIndent = first.Groups[1].Value.Length;
            var list = NewList(first.Groups[2].Value);

            while (i < lines.Length && ListItem().Match(lines[i]) is { Success: true } item)
            {
                var indent = item.Groups[1].Value.Length;
                if (indent > baseIndent + 1 && list.ListItems.LastListItem is { } parent)
                {
                    parent.Blocks.Add(ListBlock(lines, ref i));
                    continue;
                }

                if (indent < baseIndent)
                {
                    break;
                }

                var text = new StringBuilder(item.Groups[3].Value.Trim());
                i++;

                // A wrapped item continues on indented lines that aren't items themselves.
                while (i < lines.Length && lines[i].Trim().Length > 0 && !ListItem().IsMatch(lines[i]) && !StartsBlock(lines, i) && lines[i].StartsWith(' '))
                {
                    text.Append(' ').Append(lines[i].Trim());
                    i++;
                }

                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
                AddInlines(paragraph.Inlines, text.ToString());
                list.ListItems.Add(new ListItem(paragraph));
            }

            return list;
        }

        private List NewList(string marker) => new()
        {
            MarkerStyle = char.IsDigit(marker[0]) ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(22, 0, 0, 0),
        };

        private Table Table(List<string> rows, List<TextAlignment> alignments)
        {
            var columns = rows.Max(r => Cells(r).Count);
            var table = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 2, 0, 12),
                BorderBrush = Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
            };

            // FlowDocument tables have no auto width: share the width by each
            // column's longest cell, so a count column stays narrow.
            var parsed = rows.Select(Cells).ToList();
            for (var c = 0; c < columns; c++)
            {
                var longest = parsed.Max(r => c < r.Count ? r[c].Replace("*", string.Empty).Replace("`", string.Empty).Length : 0);
                table.Columns.Add(new TableColumn { Width = new GridLength(Math.Clamp(longest, 4, 60), GridUnitType.Star) });
            }

            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            for (var r = 0; r < rows.Count; r++)
            {
                var cells = Cells(rows[r]);
                var row = new TableRow();
                if (r == 0)
                {
                    row.Background = Brush("SurfaceBrush");
                    row.FontWeight = FontWeights.SemiBold;
                }

                for (var c = 0; c < columns; c++)
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = c < alignments.Count ? alignments[c] : TextAlignment.Left };
                    AddInlines(paragraph.Inlines, c < cells.Count ? cells[c] : string.Empty);
                    row.Cells.Add(new TableCell(paragraph)
                    {
                        Padding = new Thickness(10, 5, 10, 5),
                        BorderBrush = Brush("BorderBrush"),
                        BorderThickness = new Thickness(0, 0, c < columns - 1 ? 1 : 0, r < rows.Count - 1 ? 1 : 0),
                    });
                }

                group.Rows.Add(row);
            }

            return table;
        }

        private List<string> Cells(string row)
        {
            var inner = row.Trim();
            if (inner.StartsWith('|'))
            {
                inner = inner[1..];
            }

            if (inner.EndsWith('|'))
            {
                inner = inner[..^1];
            }

            return inner.Split('|').Select(c => c.Trim()).ToList();
        }

        private TextAlignment Alignment(string separator)
        {
            var s = separator.Trim();
            return (s.StartsWith(':'), s.EndsWith(':')) switch
            {
                (true, true) => TextAlignment.Center,
                (false, true) => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
        }

        private Block CodeBlock(string code) => new Paragraph(new Run(code))
        {
            FontFamily = Resource<FontFamily>("MonoFont") ?? new FontFamily("Consolas"),
            FontSize = 12.5,
            LineHeight = 18,
            Background = Brush("SurfaceBrush"),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 10),
        };

        /// <summary>**bold**, *italic*, `code` and [links](…) within a line.</summary>
        private void AddInlines(InlineCollection inlines, string text)
        {
            var position = 0;
            foreach (Match m in InlineToken().Matches(text))
            {
                if (m.Index > position)
                {
                    inlines.Add(new Run(text[position..m.Index]));
                }

                var token = m.Value;
                if (token.StartsWith("**", StringComparison.Ordinal) || token.StartsWith("__", StringComparison.Ordinal))
                {
                    var bold = new Bold();
                    AddInlines(bold.Inlines, token[2..^2]);
                    inlines.Add(bold);
                }
                else if (token.StartsWith('`'))
                {
                    inlines.Add(new Run(token[1..^1])
                    {
                        FontFamily = Resource<FontFamily>("MonoFont") ?? new FontFamily("Consolas"),
                        FontSize = 12.5,
                        Background = Brush("SurfaceBrush"),
                    });
                }
                else if (token.StartsWith('['))
                {
                    var link = LinkToken().Match(token);
                    inlines.Add(new Underline(new Run(link.Groups[1].Value)) { ToolTip = link.Groups[2].Value });
                }
                else
                {
                    var italic = new Italic();
                    AddInlines(italic.Inlines, token[1..^1]);
                    inlines.Add(italic);
                }

                position = m.Index + m.Length;
            }

            if (position < text.Length)
            {
                inlines.Add(new Run(text[position..]));
            }
        }

        private Brush Brush(string key) => Resource<Brush>(key) ?? Brushes.Gray;

        private T? Resource<T>(string key)
            where T : class => (scope?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as T;

    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();

    // Group 1 indent, 2 marker, 3 text.
    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    // Bold before italic; single underscores only at word edges, so
    // hostnames like core_sw_01 stay as they are.
    [GeneratedRegex(@"\*\*(?=\S).+?(?<=\S)\*\*|__(?=\S).+?(?<=\S)__|`[^`]+`|\[[^\]]+\]\([^)\s]+\)|(?<![\w*])\*(?=\S)[^*]+?(?<=\S)\*(?![\w*])|(?<!\w)_(?=\S)[^_]+?(?<=\S)_(?!\w)")]
    private static partial Regex InlineToken();

    [GeneratedRegex(@"^\[([^\]]+)\]\(([^)\s]+)\)$")]
    private static partial Regex LinkToken();
}

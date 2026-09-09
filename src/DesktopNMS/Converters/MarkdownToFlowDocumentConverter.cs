using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopNMS.Converters;

/// <summary>
/// Renders a small, common subset of Markdown - headings, bullet/numbered
/// lists, horizontal rules, bold/italic/code spans - as a WPF
/// <see cref="FlowDocument"/>. Built for GitHub release notes rather than as
/// a general CommonMark implementation: good enough for what a release body
/// actually contains, not a full parser.
/// </summary>
public sealed class MarkdownToFlowDocumentConverter : IValueConverter
{
    private static readonly Color TextPrimary = Color.FromRgb(0xE6, 0xEA, 0xF0);
    private static readonly Color TextSecondary = Color.FromRgb(0x98, 0xA2, 0xB3);
    private static readonly Color SurfaceAlt = Color.FromRgb(0x1E, 0x24, 0x30);
    private static readonly Color Border = Color.FromRgb(0x2C, 0x34, 0x42);

    private static readonly Regex BoldPattern = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex ItalicPattern = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)|(?<!_)_(?!_)(.+?)(?<!_)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex CodePattern = new(@"`([^`]+)`", RegexOptions.Compiled);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(TextPrimary),
            PagePadding = new System.Windows.Thickness(0),
        };

        if (value is not string markdown || string.IsNullOrWhiteSpace(markdown))
        {
            document.Blocks.Add(new Paragraph(new Run("No release notes were provided for this version.")) { Foreground = new SolidColorBrush(TextSecondary) });
            return document;
        }

        // Release notes use \n only; a stray \r would otherwise count as part
        // of the line's text and break the regexes below.
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (Regex.IsMatch(line, @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                document.Blocks.Add(new BlockUIContainer(new System.Windows.Controls.Border
                {
                    BorderBrush = new SolidColorBrush(Border),
                    BorderThickness = new System.Windows.Thickness(0, 1, 0, 0),
                    Margin = new System.Windows.Thickness(0, 6, 0, 6),
                }));
                continue;
            }

            var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (headingMatch.Success)
            {
                var level = headingMatch.Groups[1].Value.Length;
                var paragraph = new Paragraph
                {
                    FontSize = level switch { 1 => 18, 2 => 16, _ => 14 },
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Margin = new System.Windows.Thickness(0, level == 1 ? 0 : 12, 0, 6),
                };
                AddInlines(paragraph.Inlines, headingMatch.Groups[2].Value);
                document.Blocks.Add(paragraph);
                continue;
            }

            var bulletMatch = Regex.Match(line, @"^\s*[-*]\s+(.*)$");
            var numberedMatch = bulletMatch.Success ? null : Regex.Match(line, @"^\s*(\d+)\.\s+(.*)$");
            if (bulletMatch.Success || (numberedMatch?.Success ?? false))
            {
                var marker = bulletMatch.Success ? "•" : numberedMatch!.Groups[1].Value + ".";
                var text = bulletMatch.Success ? bulletMatch.Groups[1].Value : numberedMatch!.Groups[2].Value;

                var paragraph = new Paragraph { Margin = new System.Windows.Thickness(18, 2, 0, 2) };
                paragraph.Inlines.Add(new Run(marker + " ") { Foreground = new SolidColorBrush(TextSecondary) });
                AddInlines(paragraph.Inlines, text);
                document.Blocks.Add(paragraph);
                continue;
            }

            var plain = new Paragraph { Margin = new System.Windows.Thickness(0, 2, 0, 2) };
            AddInlines(plain.Inlines, line);
            document.Blocks.Add(plain);
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("No release notes were provided for this version.")) { Foreground = new SolidColorBrush(TextSecondary) });
        }

        return document;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    /// <summary>Splits a line into bold/italic/code/plain runs, in the order they appear.</summary>
    private static void AddInlines(InlineCollection inlines, string text)
    {
        var position = 0;

        while (position < text.Length)
        {
            var bold = BoldPattern.Match(text, position);
            var italic = ItalicPattern.Match(text, position);
            var code = CodePattern.Match(text, position);

            var next = new[] { bold, italic, code }
                .Where(m => m.Success)
                .OrderBy(m => m.Index)
                .FirstOrDefault();

            if (next is null || !next.Success)
            {
                inlines.Add(new Run(text[position..]));
                return;
            }

            if (next.Index > position)
            {
                inlines.Add(new Run(text[position..next.Index]));
            }

            if (next == bold)
            {
                var content = bold.Groups[1].Success ? bold.Groups[1].Value : bold.Groups[2].Value;
                inlines.Add(new Bold(new Run(content)));
            }
            else if (next == italic)
            {
                var content = italic.Groups[1].Success ? italic.Groups[1].Value : italic.Groups[2].Value;
                inlines.Add(new Italic(new Run(content)));
            }
            else
            {
                inlines.Add(new Run(code.Groups[1].Value)
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = new SolidColorBrush(SurfaceAlt),
                });
            }

            position = next.Index + next.Length;
        }
    }
}

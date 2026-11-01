using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using DesktopNMS.Services.Intelligence;
using SkiaSharp;

namespace DesktopNMS.Services;

/// <summary>
/// An Intelligence chat as a PDF (#315), drawn with Skia, which Avalonia
/// already ships. The text is real, selectable text laid out for A4 pages, not
/// a picture of the chat, and web sources stay clickable. Characters the
/// app's fonts don't have use a system font that does.
/// </summary>
public sealed partial class IntelligenceChatPdfWriter : IIntelligenceChatPdfWriter
{
    // A4, in points.
    private const float PageWidth = 595.28f;
    private const float PageHeight = 841.89f;
    private const float Margin = 54f;
    private const float FooterHeight = 26f;
    private const float ContentWidth = PageWidth - 2 * Margin;
    private const float BodySize = 10.5f;

    // Printed on white, so a dark ink palette rather than the chat's navy one.
    private static readonly SKColor Ink = new(0x1B, 0x24, 0x30);
    private static readonly SKColor Subtle = new(0x5B, 0x6B, 0x7F);
    private static readonly SKColor Accent = new(0x1F, 0x6F, 0xD1);
    private static readonly SKColor LinkColour = new(0x1A, 0x5F, 0xB4);
    private static readonly SKColor RuleColour = new(0xD5, 0xDC, 0xE5);
    private static readonly SKColor Shade = new(0xF1, 0xF4, 0xF8);
    private static readonly SKColor NoteBar = new(0xE0, 0xA0, 0x2E);

    private static readonly Lazy<Typefaces> Shared = new(Typefaces.Load);

    public void Write(ExportedChat chat, Stream output)
    {
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.Title = chat.Title;
        metadata.Creator = "DashyNMS";
        metadata.Producer = "DashyNMS";
        using var document = SKDocument.CreatePdf(output, metadata);
        using var layout = new Layout(document, Shared.Value, chat.Title);
        layout.Chat(chat);
        layout.Finish();
        document.Close();
    }

    /// <summary>The bundled IBM Plex fonts, or the system's own if they can't be read.</summary>
    private sealed class Typefaces
    {
        public required SKTypeface Regular { get; init; }
        public required SKTypeface SemiBold { get; init; }
        public required SKTypeface Mono { get; init; }

        public static Typefaces Load() => new()
        {
            Regular = Asset("IBMPlexSans-Regular.ttf", "sans-serif", SKFontStyle.Normal),
            SemiBold = Asset("IBMPlexSans-SemiBold.ttf", "sans-serif", SKFontStyle.Bold),
            Mono = Asset("IBMPlexMono-Regular.ttf", "monospace", SKFontStyle.Normal),
        };

        private static SKTypeface Asset(string file, string family, SKFontStyle style)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://DashyNMS/Assets/Fonts/" + file));
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                if (SKTypeface.FromData(SKData.CreateCopy(copy.ToArray())) is { } typeface) return typeface;
            }
            catch (Exception)
            {
                // Outside the app (tests, or a damaged install): a system font still gives a readable PDF.
            }

            return SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
        }
    }

    private readonly record struct Piece(string Text, SKFont Font, SKColor Colour, Uri? Link, bool Code, float Width);

    private sealed class Line
    {
        public List<(Piece Piece, float X)> Pieces { get; } = new();
        public float Width { get; set; }
    }

    /// <summary>The text style a block asks for; spans add bold, italic, code and links on top.</summary>
    private readonly record struct TextStyle(float Size, SKColor Colour, bool Bold = false, bool Italic = false, float LineHeight = 1.45f);

    private sealed class Layout : IDisposable
    {
        private const float Bottom = PageHeight - Margin - FooterHeight;

        private readonly SKDocument _document;
        private readonly Typefaces _typefaces;
        private readonly string _title;
        private readonly Dictionary<(SKTypeface Typeface, float Size, bool Italic), SKFont> _fonts = new();
        private readonly Dictionary<int, SKTypeface?> _fallbacks = new();
        private readonly SKPaint _fill = new() { IsAntialias = true };
        private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f };
        private SKCanvas? _canvas;
        private int _page;
        private float _y;

        public Layout(SKDocument document, Typefaces typefaces, string title)
        {
            _document = document;
            _typefaces = typefaces;
            _title = title;
        }

        private SKCanvas Canvas => _canvas ?? NewPage();

        public void Chat(ExportedChat chat)
        {
            NewPage();
            Paragraph(Plain(chat.Title), new TextStyle(20, Ink, Bold: true, LineHeight: 1.3f));
            _y += 2;
            Paragraph(Plain(chat.Summary), new TextStyle(9, Subtle));
            _y += 8;
            HorizontalRule();

            foreach (var message in chat.Messages)
            {
                _y += 10;
                MessageHeading(message);
                if (message.IsUser)
                {
                    // A question is what was typed, so it isn't read as Markdown.
                    foreach (var paragraph in message.Markdown.Replace("\r\n", "\n").Split('\n'))
                    {
                        Paragraph(Plain(paragraph.Length == 0 ? " " : paragraph), new TextStyle(BodySize, Ink), bar: Accent);
                    }

                    _y += 6;
                    continue;
                }

                if (message.Note is { } note)
                {
                    Paragraph(Plain(note), new TextStyle(9.5f, Subtle, Italic: true), bar: NoteBar);
                    _y += 6;
                }

                foreach (var image in message.Images) Image(image);
                Blocks(ChatMarkdownBlocks.Parse(message.Markdown));
            }
        }

        public void Finish()
        {
            if (_canvas is null) return;
            Footer();
            _document.EndPage();
            _canvas = null;
        }

        private SKCanvas NewPage()
        {
            if (_canvas is not null)
            {
                Footer();
                _document.EndPage();
            }

            _canvas = _document.BeginPage(PageWidth, PageHeight);
            _page++;
            _y = Margin;
            return _canvas;
        }

        /// <summary>Starts a new page unless this much room is left. A page that's still empty takes it anyway.</summary>
        private void Room(float height)
        {
            if (_canvas is null) NewPage();
            else if (_y + height > Bottom && _y > Margin) NewPage();
        }

        private void Footer()
        {
            var text = Truncate(_title, Font(_typefaces.Regular, 8, false), ContentWidth - 60) + " · Page " + _page;
            var x = Margin;
            foreach (var piece in Runs(text, _typefaces.Regular, 8, false, Subtle, null, false))
            {
                _fill.Color = Subtle;
                _canvas!.DrawText(piece.Text, x, PageHeight - Margin + 4, piece.Font, _fill);
                x += piece.Width;
            }
        }

        private void MessageHeading(ExportedChatMessage message)
        {
            var label = message.IsUser ? ExportedChat.UserLabel : ExportedChat.AssistantLabel;
            var spans = new List<ChatTextSpan> { new(label, ChatTextStyle.Bold) };
            var heading = ExportedChat.Heading(message);
            if (heading.Length > label.Length) spans.Add(new(heading[label.Length..], ChatTextStyle.None));

            // Keep a heading with at least a couple of lines of what follows.
            Room(BodySize * 1.45f * 3);
            Paragraph(spans, new TextStyle(10, message.IsUser ? Subtle : Accent));
            _y += 3;
        }

        private void Blocks(IReadOnlyList<ChatBlock> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case ChatHeadingBlock heading:
                        _y += 4;
                        Room(40);
                        Paragraph(heading.Spans, new TextStyle(heading.Level switch { 1 => 14, 2 => 12.5f, _ => 11.5f }, Ink, Bold: true, LineHeight: 1.35f));
                        _y += 3;
                        break;
                    case ChatParagraphBlock paragraph:
                        Paragraph(paragraph.Spans, new TextStyle(BodySize, Ink));
                        _y += 6;
                        break;
                    case ChatQuoteBlock quote:
                        Paragraph(quote.Spans, new TextStyle(BodySize, Subtle), bar: RuleColour);
                        _y += 6;
                        break;
                    case ChatListItemBlock item:
                        Paragraph(item.Spans, new TextStyle(BodySize, Ink), indent: 4 + 16 * item.Depth, marker: item.Marker);
                        _y += 2;
                        break;
                    case ChatCodeBlock code:
                        Code(code.Text);
                        _y += 6;
                        break;
                    case ChatTableBlock table:
                        Table(table);
                        _y += 8;
                        break;
                    case ChatRuleBlock:
                        _y += 2;
                        HorizontalRule();
                        _y += 4;
                        break;
                }
            }
        }

        private void HorizontalRule()
        {
            Room(6);
            _stroke.Color = RuleColour;
            Canvas.DrawLine(Margin, _y + 2, Margin + ContentWidth, _y + 2, _stroke);
            _y += 6;
        }

        /// <summary>Wrapped text, page by page. A bar down the left marks a question, note or quote; a marker hangs to the left of a list item.</summary>
        private void Paragraph(IReadOnlyList<ChatTextSpan> spans, TextStyle style, SKColor? bar = null, float indent = 0, string? marker = null)
        {
            var left = Margin + indent + (bar is null ? 0 : 10) + (marker is null ? 0 : 16);
            var lines = Wrap(spans, style, Margin + ContentWidth - left);
            var lineHeight = style.Size * style.LineHeight;
            for (var i = 0; i < lines.Count; i++)
            {
                Room(lineHeight);
                if (bar is { } colour)
                {
                    _fill.Color = colour;
                    Canvas.DrawRect(SKRect.Create(Margin + indent, _y, 2.5f, lineHeight), _fill);
                }

                if (i == 0 && marker is not null)
                {
                    var font = Font(_typefaces.Regular, style.Size, false);
                    _fill.Color = style.Colour;
                    Canvas.DrawText(marker, Margin + indent, Baseline(font, lineHeight), font, _fill);
                }

                DrawLine(lines[i], left, lineHeight, ChatColumnAlignment.Left, Margin + ContentWidth - left);
                _y += lineHeight;
            }
        }

        private void DrawLine(Line line, float left, float lineHeight, ChatColumnAlignment alignment, float width)
        {
            var offset = alignment switch
            {
                ChatColumnAlignment.Right => width - line.Width,
                ChatColumnAlignment.Centre => (width - line.Width) / 2,
                _ => 0,
            };

            foreach (var (piece, x) in line.Pieces)
            {
                var start = left + offset + x;
                var baseline = Baseline(piece.Font, lineHeight);
                if (piece.Code)
                {
                    _fill.Color = Shade;
                    Canvas.DrawRect(SKRect.Create(start - 1, _y + 1, piece.Width + 2, lineHeight - 2), _fill);
                }

                _fill.Color = piece.Colour;
                Canvas.DrawText(piece.Text, start, baseline, piece.Font, _fill);
                if (piece.Link is { } link)
                {
                    _stroke.Color = piece.Colour;
                    Canvas.DrawLine(start, baseline + 1.5f, start + piece.Width, baseline + 1.5f, _stroke);
                    Canvas.DrawUrlAnnotation(SKRect.Create(start, _y, piece.Width, lineHeight), link.AbsoluteUri);
                }
            }
        }

        private float Baseline(SKFont font, float lineHeight)
        {
            var metrics = font.Metrics;
            var textHeight = metrics.Descent - metrics.Ascent;
            return _y + (lineHeight - textHeight) / 2 - metrics.Ascent;
        }

        /// <summary>Lines no wider than <paramref name="width"/>, breaking between words, and inside a word only when it's wider than a whole line.</summary>
        private List<Line> Wrap(IReadOnlyList<ChatTextSpan> spans, TextStyle style, float width)
        {
            var lines = new List<Line> { new() };
            var pendingSpace = 0f;
            foreach (var span in spans)
            {
                foreach (Match word in WordOrSpace().Matches(span.Text))
                {
                    if (char.IsWhiteSpace(word.Value[0]))
                    {
                        if (lines[^1].Pieces.Count > 0) pendingSpace = Font(span, style).MeasureText(" ");
                        continue;
                    }

                    var pieces = Pieces(word.Value, span, style);
                    var wordWidth = pieces.Sum(piece => piece.Width);
                    if (lines[^1].Pieces.Count > 0 && lines[^1].Width + pendingSpace + wordWidth > width)
                    {
                        lines.Add(new());
                        pendingSpace = 0;
                    }

                    if (wordWidth > width)
                    {
                        // A long address or hostname: break it wherever the line runs out.
                        foreach (var piece in pieces.SelectMany(piece => Split(piece, width)))
                        {
                            if (lines[^1].Pieces.Count > 0 && lines[^1].Width + piece.Width > width) lines.Add(new());
                            Place(lines[^1], piece, 0);
                        }
                    }
                    else
                    {
                        foreach (var piece in pieces)
                        {
                            Place(lines[^1], piece, pendingSpace);
                            pendingSpace = 0;
                        }
                    }

                    pendingSpace = 0;
                }
            }

            return lines;
        }

        private static void Place(Line line, Piece piece, float space)
        {
            line.Pieces.Add((piece, line.Width + space));
            line.Width += space + piece.Width;
        }

        private static IEnumerable<Piece> Split(Piece piece, float width)
        {
            var start = 0;
            var text = piece.Text;
            while (start < text.Length)
            {
                var length = 1;
                while (start + length < text.Length && piece.Font.MeasureText(text.Substring(start, length + 1)) <= width) length++;
                if (char.IsHighSurrogate(text[start + length - 1]) && start + length < text.Length) length++;
                var part = text.Substring(start, length);
                yield return piece with { Text = part, Width = piece.Font.MeasureText(part) };
                start += length;
            }
        }

        /// <summary>A word in its span's style.</summary>
        private List<Piece> Pieces(string text, ChatTextSpan span, TextStyle style)
        {
            var code = span.Style.HasFlag(ChatTextStyle.Code);
            return Runs(text, Typeface(span, style), code ? style.Size * 0.9f : style.Size,
                style.Italic || span.Style.HasFlag(ChatTextStyle.Italic),
                span.Link is not null ? LinkColour : style.Colour, span.Link, code);
        }

        /// <summary>Text split where a character needs a fallback font: a name in another script, a symbol, an emoji.</summary>
        private List<Piece> Runs(string text, SKTypeface primary, float size, bool italic, SKColor colour, Uri? link, bool code)
        {
            var pieces = new List<Piece>();
            var run = new StringBuilder();
            SKTypeface? current = null;

            void Flush()
            {
                if (run.Length == 0 || current is null) return;
                var font = Font(current, size, italic);
                var value = run.ToString();
                pieces.Add(new Piece(value, font, colour, link, code, font.MeasureText(value)));
                run.Clear();
            }

            foreach (var rune in text.EnumerateRunes())
            {
                var typeface = rune.Value < 0x20 || primary.ContainsGlyph(rune.Value) ? primary : Fallback(rune.Value) ?? primary;
                if (current is not null && typeface != current) Flush();
                current = typeface;
                run.Append(rune.ToString());
            }

            Flush();
            return pieces;
        }

        private SKTypeface Typeface(ChatTextSpan span, TextStyle style)
            => span.Style.HasFlag(ChatTextStyle.Code) ? _typefaces.Mono
                : style.Bold || span.Style.HasFlag(ChatTextStyle.Bold) ? _typefaces.SemiBold
                : _typefaces.Regular;

        private SKFont Font(ChatTextSpan span, TextStyle style)
            => Font(Typeface(span, style), span.Style.HasFlag(ChatTextStyle.Code) ? style.Size * 0.9f : style.Size,
                style.Italic || span.Style.HasFlag(ChatTextStyle.Italic));

        private SKFont Font(SKTypeface typeface, float size, bool italic)
        {
            if (!_fonts.TryGetValue((typeface, size, italic), out var font))
            {
                // The bundled fonts have no italic, so it's slanted instead.
                font = new SKFont(typeface, size, 1f, italic ? -0.2f : 0f) { Subpixel = true };
                _fonts[(typeface, size, italic)] = font;
            }

            return font;
        }

        private SKTypeface? Fallback(int codepoint)
        {
            if (!_fallbacks.TryGetValue(codepoint, out var typeface))
            {
                typeface = SKFontManager.Default.MatchCharacter(codepoint);
                _fallbacks[codepoint] = typeface;
            }

            return typeface;
        }

        private void Image(ExportedChatImage image)
        {
            using var decoded = SKImage.FromEncodedData(image.Data);
            if (decoded is null)
            {
                Paragraph(Plain("(A graph in this answer couldn't be included in the PDF.)"), new TextStyle(9.5f, Subtle, Italic: true));
                _y += 6;
                return;
            }

            // Graphs come at screen size: 1 pixel is drawn as 0.75 pt, shrunk to fit the page and a sensible height.
            var width = Math.Min(ContentWidth, decoded.Width * 0.75f);
            var height = width * decoded.Height / Math.Max(1, decoded.Width);
            const float maxHeight = 340f;
            if (height > maxHeight)
            {
                width *= maxHeight / height;
                height = maxHeight;
            }

            Room(height + 4);
            var area = SKRect.Create(Margin + 1, _y + 1, width, height);
            Canvas.DrawImage(decoded, area);
            _stroke.Color = RuleColour;
            Canvas.DrawRect(SKRect.Inflate(area, 0.5f, 0.5f), _stroke);
            _y += height + 10;
        }

        private void Code(string text)
        {
            const float padding = 6f;
            var font = Font(_typefaces.Mono, 8.5f, false);
            var lineHeight = 8.5f * 1.45f;
            var width = ContentWidth - 2 * padding;
            var lines = new List<string>();
            foreach (var source in text.Replace("\t", "    ").Split('\n'))
            {
                if (source.Length == 0)
                {
                    lines.Add(string.Empty);
                    continue;
                }

                var piece = new Piece(source, font, Ink, null, false, font.MeasureText(source));
                lines.AddRange(Split(piece, width).Select(part => part.Text));
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var top = i == 0 ? padding : 0;
                var bottom = i == lines.Count - 1 ? padding : 0;
                Room(lineHeight + top + bottom);
                _fill.Color = Shade;
                Canvas.DrawRect(SKRect.Create(Margin, _y, ContentWidth, lineHeight + top + bottom), _fill);
                _y += top;
                var x = Margin + padding;
                foreach (var piece in Runs(lines[i], _typefaces.Mono, 8.5f, false, Ink, null, false))
                {
                    _fill.Color = Ink;
                    Canvas.DrawText(piece.Text, x, Baseline(piece.Font, lineHeight), piece.Font, _fill);
                    x += piece.Width;
                }

                _y += lineHeight + bottom;
            }
        }

        private void Table(ChatTableBlock table)
        {
            const float padX = 6f;
            const float padY = 4f;
            var columns = table.Rows.Max(row => row.Count);
            if (columns == 0) return;

            // Columns share the width by their longest cell, as in the chat, so a count column stays narrow.
            var weights = Enumerable.Range(0, columns)
                .Select(c => (float)Math.Clamp(table.Rows.Max(row => c < row.Count ? row[c].Sum(span => span.Text.Length) : 0), 4, 60))
                .ToArray();
            var total = weights.Sum();
            var widths = weights.Select(weight => ContentWidth * weight / total).ToArray();
            var size = 9f;
            var lineHeight = size * 1.4f;

            List<Line>[] Cells(IReadOnlyList<IReadOnlyList<ChatTextSpan>> row, bool header)
                => Enumerable.Range(0, columns)
                    .Select(c => Wrap(c < row.Count ? row[c] : Array.Empty<ChatTextSpan>(), new TextStyle(size, Ink, Bold: header), widths[c] - 2 * padX))
                    .ToArray();

            var headerCells = Cells(table.Rows[0], true);
            for (var r = 0; r < table.Rows.Count; r++)
            {
                var cells = r == 0 ? headerCells : Cells(table.Rows[r], false);
                var height = cells.Max(cell => cell.Count) * lineHeight + 2 * padY;
                if (_y + height > Bottom && _y > Margin)
                {
                    NewPage();
                    // The header row again at the top of the new page.
                    if (r > 0) Row(headerCells, true, HeightOf(headerCells));
                }

                Row(cells, r == 0, height);
            }

            float HeightOf(List<Line>[] cells) => cells.Max(cell => cell.Count) * lineHeight + 2 * padY;

            void Row(List<Line>[] cells, bool header, float height)
            {
                var top = _y;
                if (header)
                {
                    _fill.Color = Shade;
                    Canvas.DrawRect(SKRect.Create(Margin, top, ContentWidth, height), _fill);
                }

                var x = Margin;
                for (var c = 0; c < columns; c++)
                {
                    _y = top + padY;
                    var alignment = c < table.Alignments.Count ? table.Alignments[c] : ChatColumnAlignment.Left;
                    foreach (var line in cells[c])
                    {
                        DrawLine(line, x + padX, lineHeight, alignment, widths[c] - 2 * padX);
                        _y += lineHeight;
                    }

                    x += widths[c];
                }

                _stroke.Color = RuleColour;
                Canvas.DrawRect(SKRect.Create(Margin, top, ContentWidth, height), _stroke);
                x = Margin;
                for (var c = 0; c < columns - 1; c++)
                {
                    x += widths[c];
                    Canvas.DrawLine(x, top, x, top + height, _stroke);
                }

                _y = top + height;
            }
        }

        private static IReadOnlyList<ChatTextSpan> Plain(string text) => new[] { new ChatTextSpan(text, ChatTextStyle.None) };

        private static string Truncate(string text, SKFont font, float width)
        {
            if (font.MeasureText(text) <= width) return text;
            var length = text.Length;
            while (length > 1 && font.MeasureText(text[..length] + "…") > width) length--;
            return text[..length].TrimEnd() + "…";
        }

        public void Dispose()
        {
            foreach (var font in _fonts.Values) font.Dispose();
            _fill.Dispose();
            _stroke.Dispose();
        }
    }

    [GeneratedRegex(@"\s+|\S+")]
    private static partial Regex WordOrSpace();
}

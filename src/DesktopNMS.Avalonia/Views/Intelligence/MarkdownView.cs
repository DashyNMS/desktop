using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Intelligence;

/// <summary>
/// Shows Markdown - what the Intelligence agent answers in - as selectable
/// text (#250, from the WPF app's MarkdownDocument). Covers what a chat answer
/// uses: paragraphs, headings, bullet and numbered lists, tables (with column
/// alignment), fenced code, quotes, rules, and bold, italic, inline code,
/// HTTP(S) source links and resolved [[device:…]]-style references. While an
/// answer streams in, it's redrawn at most every 120 ms. Colours are the
/// answer card's own navy palette, the same in both themes.
/// </summary>
public sealed partial class MarkdownView : Decorator
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<string?> ReplyStyleProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(ReplyStyle));

    public static readonly StyledProperty<bool> ColourStatusIndicatorsProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(ColourStatusIndicators), true);

    public static readonly StyledProperty<IReadOnlyList<IntelligenceInlineReference>?> InlineReferencesProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<IntelligenceInlineReference>?>(nameof(InlineReferences));

    private static readonly IBrush TextPrimary = new ImmutableSolidColorBrush(Color.Parse("#EEF5FF"));
    private static readonly IBrush TextSecondary = new ImmutableSolidColorBrush(Color.Parse("#AEC5DF"));
    private static readonly IBrush Link = new ImmutableSolidColorBrush(Color.Parse("#8CCBFF"));
    private static readonly IBrush Surface = new ImmutableSolidColorBrush(Color.Parse("#111F33"));
    private static readonly IBrush Rule = new ImmutableSolidColorBrush(Color.Parse("#34516E"));

    private DispatcherTimer? _timer;
    private bool _rendered;

    /// <summary>A source link was clicked - the host decides whether and how to open it.</summary>
    public event EventHandler<Uri>? LinkActivated;
    public event EventHandler<IntelligenceReferenceAction>? ObjectActionRequested;
    public static readonly StyledProperty<bool> ObjectActionsEnabledProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(ObjectActionsEnabled), true);
    public bool ObjectActionsEnabled { get => GetValue(ObjectActionsEnabledProperty); set => SetValue(ObjectActionsEnabledProperty, value); }

    public string? Markdown { get => GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    public string? ReplyStyle { get => GetValue(ReplyStyleProperty); set => SetValue(ReplyStyleProperty, value); }

    public bool ColourStatusIndicators { get => GetValue(ColourStatusIndicatorsProperty); set => SetValue(ColourStatusIndicatorsProperty, value); }

    public IReadOnlyList<IntelligenceInlineReference>? InlineReferences { get => GetValue(InlineReferencesProperty); set => SetValue(InlineReferencesProperty, value); }

    private bool Conversational => ReplyStyle is { } detail && IntelligenceDetail.IsConversational(detail);

    private IReadOnlyList<IntelligenceInlineReference> References => InlineReferences ?? Array.Empty<IntelligenceInlineReference>();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            // The first text draws straight away; changes after that are batched.
            if (!_rendered)
            {
                Render();
                return;
            }

            if (_timer is null)
            {
                _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
                {
                    _timer!.Stop();
                    Render();
                });
            }

            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else if (change.Property == ReplyStyleProperty || change.Property == ColourStatusIndicatorsProperty || change.Property == InlineReferencesProperty)
        {
            if (_rendered)
            {
                Render();
            }
        }
    }

    private void Render()
    {
        _rendered = true;
        var blocks = new StackPanel();
        var lines = (Markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');
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
                blocks.Children.Add(CodeBlock(code.ToString()));
                continue;
            }

            // # Heading
            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                var level = heading.Groups[1].Value.Length;
                var text = Paragraph(new Thickness(0, 6, 0, 6));
                text.FontWeight = FontWeight.SemiBold;
                text.FontSize = Conversational ? 16 : level switch { 1 => 19, 2 => 17, _ => 15 };
                AddInlines(text.Inlines!, heading.Groups[2].Value);
                blocks.Children.Add(text);
                i++;
                continue;
            }

            // --- rule
            if (RuleLine().IsMatch(trimmed))
            {
                blocks.Children.Add(new Border { Height = 1, Background = Rule, Margin = new Thickness(0, 6, 0, 10) });
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

                blocks.Children.Add(Table(rows, alignments));
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

                var text = Paragraph(default);
                text.Foreground = TextSecondary;
                AddInlines(text.Inlines!, quote.ToString().Trim());
                blocks.Children.Add(new Border
                {
                    BorderBrush = Rule,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 10),
                    Child = text,
                });
                continue;
            }

            // - bullets / 1. numbers
            if (ListItem().IsMatch(line))
            {
                blocks.Children.Add(ListBlock(lines, ref i));
                continue;
            }

            // A paragraph: lines up to the next blank line or block.
            var prose = new StringBuilder(trimmed);
            i++;
            while (i < lines.Length && lines[i].Trim().Length > 0 && !StartsBlock(lines, i))
            {
                prose.Append(' ').Append(lines[i].Trim());
                i++;
            }

            blocks.Children.Add(ProseBlock(prose.ToString()));
        }

        // No space under the last block.
        if (blocks.Children.LastOrDefault() is Control last)
        {
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }

        Child = blocks;
    }

    /// <summary>A paragraph, or one of the agent's marked asides: a note or update in small print, or a recommendation with a bar beside it.</summary>
    private Control ProseBlock(string prose)
    {
        var bottom = Conversational ? 14 : 10;
        var updatePrefix = prose.StartsWith("[[update]]", StringComparison.Ordinal) ? "[[update]]"
            : prose.StartsWith("**Notes:**", StringComparison.Ordinal) ? "**Notes:**"
            : prose.StartsWith("**Notes**:", StringComparison.Ordinal) ? "**Notes**:"
            : prose.StartsWith("Notes:", StringComparison.Ordinal) ? "Notes:"
            : prose.StartsWith("**Update:**", StringComparison.Ordinal) ? "**Update:**"
            : prose.StartsWith("**Update**:", StringComparison.Ordinal) ? "**Update**:"
            : prose.StartsWith("Update:", StringComparison.Ordinal) ? "Update:" : null;

        if (updatePrefix is not null)
        {
            var notes = Paragraph(default);
            notes.FontSize = 12.5;
            notes.LineHeight = 18;
            notes.Foreground = TextSecondary;
            notes.Inlines!.Add(new Bold { Inlines = { new Run("Notes: ") } });
            AddInlines(notes.Inlines, prose[updatePrefix.Length..].TrimStart());
            return new Border
            {
                BorderBrush = Rule,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 8, 0, 0),
                Margin = new Thickness(0, 10, 0, 0),
                Child = notes,
            };
        }

        if (prose.StartsWith("[[recommendation]]", StringComparison.Ordinal))
        {
            var recommendation = Paragraph(default);
            recommendation.Inlines!.Add(new Bold { Inlines = { new Run("Recommendation. ") } });
            AddInlines(recommendation.Inlines, prose["[[recommendation]]".Length..].TrimStart());
            return new Border
            {
                BorderBrush = Link,
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(12, 0, 0, 0),
                Margin = new Thickness(0, 0, 0, bottom),
                Child = recommendation,
            };
        }

        var paragraph = Paragraph(new Thickness(0, 0, 0, bottom));
        AddInlines(paragraph.Inlines!, prose);
        return paragraph;
    }

    private SelectableTextBlock Paragraph(Thickness margin) => new()
    {
        Margin = margin,
        TextWrapping = TextWrapping.Wrap,
        FontSize = Conversational ? 15 : 14,
        LineHeight = Conversational ? 24 : 21,
        Foreground = TextPrimary,
        Inlines = new InlineCollection(),
    };

    private bool StartsBlock(string[] lines, int i)
    {
        var t = lines[i].Trim();
        return t.StartsWith("```", StringComparison.Ordinal)
               || Heading().IsMatch(t)
               || RuleLine().IsMatch(t)
               || t.StartsWith('>')
               || t.StartsWith("[[recommendation]]", StringComparison.Ordinal)
               || t.StartsWith("[[update]]", StringComparison.Ordinal)
               || t.StartsWith("Notes:", StringComparison.Ordinal)
               || t.StartsWith("**Notes:**", StringComparison.Ordinal)
               || t.StartsWith("**Notes**:", StringComparison.Ordinal)
               || t.StartsWith("Update:", StringComparison.Ordinal)
               || t.StartsWith("**Update:**", StringComparison.Ordinal)
               || t.StartsWith("**Update**:", StringComparison.Ordinal)
               || ListItem().IsMatch(lines[i])
               || (t.StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1].Trim()));
    }

    /// <summary>A run of list items - bullets or numbers - with nesting by indent: a marker column beside the text.</summary>
    private Control ListBlock(string[] lines, ref int i)
    {
        var first = ListItem().Match(lines[i]);
        var baseIndent = first.Groups[1].Value.Length;
        var numbered = char.IsDigit(first.Groups[2].Value[0]);
        var list = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(numbered ? 4 : 6, 0, 0, 10),
        };
        var number = 0;
        Control? lastText = null;

        while (i < lines.Length && ListItem().Match(lines[i]) is { Success: true } item)
        {
            var indent = item.Groups[1].Value.Length;
            if (indent > baseIndent + 1 && lastText is not null)
            {
                // A nested list goes under the item before it, in the text column.
                var nested = ListBlock(lines, ref i);
                nested.Margin = new Thickness(0, 2, 0, 2);
                list.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(nested, list.RowDefinitions.Count - 1);
                Grid.SetColumn(nested, 1);
                list.Children.Add(nested);
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

            var content = text.ToString();
            var prefix = IntelligenceInlineMarkup.BulletPrefix().Match(content);
            var tone = prefix.Success ? prefix.Groups[1].Value == "ok" ? "up" : prefix.Groups[1].Value : null;
            if (prefix.Success)
            {
                content = content[prefix.Length..];
            }

            // Prefer the verified alert's severity if a model-supplied bullet colour disagrees.
            var alertMarker = IntelligenceInlineMarkup.Token().Matches(content).Cast<Match>()
                .FirstOrDefault(match => match.Value.StartsWith("[[alert:", StringComparison.Ordinal));
            if (alertMarker is not null && IntelligenceInlineMarkup.Resolve(alertMarker.Value, References) is { } alertReference)
            {
                tone = alertReference.Tone;
            }

            var row = list.RowDefinitions.Count;
            list.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var marker = new TextBlock
            {
                Text = numbered ? $"{++number}." : "●",
                FontSize = numbered ? (Conversational ? 15 : 14) : 11,
                Foreground = numbered || tone is null ? TextPrimary : ToneBrush(tone),
                Margin = new Thickness(0, numbered ? 0 : 4, 8, 0),
                MinWidth = numbered ? 18 : 0,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(marker, row);
            list.Children.Add(marker);

            var paragraph = Paragraph(new Thickness(0, 0, 0, Conversational ? 8 : 3));
            AddInlines(paragraph.Inlines!, content);
            Grid.SetRow(paragraph, row);
            Grid.SetColumn(paragraph, 1);
            list.Children.Add(paragraph);
            lastText = paragraph;
        }

        return list;
    }

    private Control Table(List<string> rows, List<TextAlignment> alignments)
    {
        var parsed = rows.Select(Cells).ToList();
        var columns = parsed.Max(r => r.Count);
        var grid = new Grid();

        // Share the width by each column's longest cell, so a count column stays narrow.
        for (var c = 0; c < columns; c++)
        {
            var longest = parsed.Max(r => c < r.Count ? r[c].Replace("*", string.Empty).Replace("`", string.Empty).Length : 0);
            grid.ColumnDefinitions.Add(new ColumnDefinition(Math.Clamp(longest, 4, 60), GridUnitType.Star));
        }

        for (var r = 0; r < parsed.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var text = new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = TextPrimary,
                    TextAlignment = c < alignments.Count ? alignments[c] : TextAlignment.Left,
                    FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    Inlines = new InlineCollection(),
                };
                AddInlines(text.Inlines, c < parsed[r].Count ? parsed[r][c] : string.Empty);

                var cell = new Border
                {
                    Padding = new Thickness(10, 5),
                    Background = r == 0 ? Surface : null,
                    BorderBrush = Rule,
                    BorderThickness = new Thickness(0, 0, !Conversational && c < columns - 1 ? 1 : 0, r < parsed.Count - 1 ? 1 : 0),
                    Child = text,
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return new Border
        {
            BorderBrush = Rule,
            BorderThickness = Conversational ? new Thickness(0) : new Thickness(1),
            Margin = new Thickness(0, 2, 0, 12),
            Child = grid,
        };
    }

    private static List<string> Cells(string row)
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

    private static TextAlignment Alignment(string separator)
    {
        var s = separator.Trim();
        return (s.StartsWith(':'), s.EndsWith(':')) switch
        {
            (true, true) => TextAlignment.Center,
            (false, true) => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
    }

    private Control CodeBlock(string code) => new Border
    {
        Background = Surface,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 8),
        Margin = new Thickness(0, 0, 0, 10),
        Child = new SelectableTextBlock
        {
            Text = code,
            FontFamily = MonoFont,
            FontSize = 12.5,
            LineHeight = 18,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap,
        },
    };

    private FontFamily MonoFont => this.TryFindResource("MonoFont", out var font) && font is FontFamily family ? family : new FontFamily("Consolas");

    /// <summary>**bold**, *italic*, `code`, [links](…) and [[references]] within a line.</summary>
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
            if (token.StartsWith("[[", StringComparison.Ordinal))
            {
                AddReference(inlines, token);
            }
            else if (token.StartsWith("**", StringComparison.Ordinal) || token.StartsWith("__", StringComparison.Ordinal))
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, token[2..^2]);
                inlines.Add(bold);
            }
            else if (token.StartsWith('`'))
            {
                inlines.Add(new Run(token[1..^1]) { FontFamily = MonoFont, FontSize = 12.5, Background = Surface });
            }
            else if (token.StartsWith('['))
            {
                var link = LinkToken().Match(token);
                var title = link.Groups[1].Value;
                if (WebSearchCitations.TryGetLink(link.Groups[2].Value, out var uri))
                {
                    var button = new Button
                    {
                        Classes = { "link" },
                        Padding = default,
                        Content = new TextBlock { Text = title, Foreground = Link, TextDecorations = TextDecorations.Underline },
                    };
                    ToolTip.SetTip(button, uri.AbsoluteUri);
                    button.Click += (_, _) => LinkActivated?.Invoke(this, uri);
                    inlines.Add(new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.TextBottom });
                }
                else
                {
                    inlines.Add(new Run(title));
                }
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

    /// <summary>A verified device, alert or status as a coloured badge, with what DashyNMS saw and when in its tooltip.</summary>
    private void AddReference(InlineCollection inlines, string marker)
    {
        if (IntelligenceInlineMarkup.Resolve(marker, References) is not { } reference)
        {
            inlines.Add(new Run(IntelligenceInlineMarkup.Fallback(marker)));
            return;
        }

        var badge = new TextBlock
        {
            Background = Surface,
            Foreground = ToneBrush(reference.Tone),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(4, 0),
            Inlines = new InlineCollection { new Run(reference.Name) },
        };
        if (!string.IsNullOrEmpty(reference.Status))
        {
            badge.Inlines.Add(new Run(" · " + reference.Status) { FontSize = 12 });
        }

        ToolTip.SetTip(badge, reference.Detail + (reference.ObservedAt == default ? string.Empty : " · observed " + reference.ObservedAt.ToLocalTime().ToString("g")));
        Control content = badge;
        if (reference.Object is { IsValid: true })
        {
            var add = new MenuItem { Header = "Add to Spotlight" };
            var only = new MenuItem { Header = "Spotlight only this object" };
            var menu = new ContextMenu { ItemsSource = new[] { add, only } };
            menu.Opening += (_, _) => { add.IsEnabled = only.IsEnabled = ObjectActionsEnabled; };
            add.Click += (_, _) => { if (ObjectActionsEnabled) ObjectActionRequested?.Invoke(this, new(reference, false)); };
            only.Click += (_, _) => { if (ObjectActionsEnabled) ObjectActionRequested?.Invoke(this, new(reference, true)); };
            var button = new Button { Content = badge, Classes = { "link" }, Padding = default, MinHeight = 0, ContextMenu = menu };
            button.Click += (_, _) => menu.Open(button);
            Avalonia.Automation.AutomationProperties.SetName(button, reference.Name + ", Spotlight actions");
            ToolTip.SetTip(button, reference.Detail + " · Choose Spotlight actions; available after the answer finishes.");
            content = button;
        }
        inlines.Add(new InlineUIContainer(content) { BaselineAlignment = BaselineAlignment.TextBottom });
    }

    private IBrush ToneBrush(string tone) => !ColourStatusIndicators ? TextPrimary : tone switch
    {
        "up" => new ImmutableSolidColorBrush(Color.FromRgb(84, 214, 154)),
        "down" or "critical" => new ImmutableSolidColorBrush(Color.FromRgb(255, 95, 98)),
        "warning" => new ImmutableSolidColorBrush(Color.FromRgb(255, 194, 71)),
        _ => TextSecondary,
    };

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();

    // Group 1 indent, 2 marker, 3 text.
    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    // Bold before italic; single underscores only at word edges, so
    // hostnames like core_sw_01 stay as they are.
    [GeneratedRegex(@"\[\[(?:device|alert|sensor|group|location|status|severity|bullet):(?:[a-z]+|[1-9][0-9]{0,9})\]\]|\[\[(?:recommendation|update)\]\]|\*\*(?=\S).+?(?<=\S)\*\*|__(?=\S).+?(?<=\S)__|`[^`]+`|\[[^\]]+\]\([^)\s]+\)|(?<![\w*])\*(?=\S)[^*]+?(?<=\S)\*(?![\w*])|(?<!\w)_(?=\S)[^_]+?(?<=\S)_(?!\w)")]
    private static partial Regex InlineToken();

    [GeneratedRegex(@"^\[([^\]]+)\]\(([^)\s]+)\)$")]
    private static partial Regex LinkToken();
}

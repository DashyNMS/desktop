using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DesktopNMS.Core.Help;
using DesktopNMS.Services.Help;

namespace DesktopNMS.Views.Help;

/// <summary>
/// Draws a help page with the app's own controls (#604): its fonts, colours
/// and spacing come from HelpView's styles, so the theme, accent and any
/// scaling apply as they do everywhere else. Links are buttons, which the
/// keyboard and screen readers already know.
/// </summary>
public sealed class HelpPageView : Decorator
{
    public static readonly StyledProperty<HelpPage?> PageProperty = AvaloniaProperty.Register<HelpPageView, HelpPage?>(nameof(Page));

    private readonly Dictionary<string, Control> _headings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Bitmap> _images = [];

    public HelpPage? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    /// <summary>Where a link goes, so one that leaves the app can say so and a broken one can be plain text.</summary>
    public Func<string, HelpLink>? ResolveLink { get; set; }

    /// <summary>An image's bytes, or null to show its description.</summary>
    public Func<string, HelpImage?>? ResolveImage { get; set; }

    /// <summary>A link was chosen; its target as written.</summary>
    public event EventHandler<string>? LinkActivated;

    /// <summary>The control at a heading, or the page's title for null.</summary>
    public Control? FindHeading(string? anchor) => _headings.GetValueOrDefault(anchor ?? string.Empty) ?? _headings.GetValueOrDefault(string.Empty);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Child is null && Page is not null)
        {
            Render();
        }
    }

    /// <summary>Its window closed: its pictures go with it. Shown again, it's drawn again.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Child = null;
        _headings.Clear();
        _images.ForEach(image => image.Dispose());
        _images.Clear();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty)
        {
            Render();
        }
    }

    private void Render()
    {
        _headings.Clear();
        var blocks = new StackPanel();
        if (Page is { } page)
        {
            var title = Text("helpTitle");
            title.Inlines!.Add(new Run(page.Title));
            AutomationProperties.SetHeadingLevel(title, 1);
            title.Focusable = true;
            _headings[string.Empty] = title;
            blocks.Children.Add(title);

            // The guide's own headings, in the same order, name each one's anchor.
            var anchors = new Queue<HelpHeading>(page.Headings);
            foreach (var block in HelpBlocks.Read(page.Markdown))
            {
                blocks.Children.Add(Draw(block, anchors));
            }
        }

        Child = blocks;

        // Once the page that showed them is gone.
        var shown = blocks;
        var old = _images.ToList();
        _images.Clear();
        _images.AddRange(Images(shown));
        foreach (var image in old)
        {
            image.Dispose();
        }
    }

    private static IEnumerable<Bitmap> Images(Control control) => control switch
    {
        Image { Source: Bitmap bitmap } => [bitmap],
        Panel panel => panel.Children.SelectMany(Images),
        Decorator { Child: { } child } => Images(child),
        _ => [],
    };

    private Control Draw(HelpBlock block, Queue<HelpHeading> anchors)
    {
        switch (block)
        {
            case HelpHeadingBlock heading:
            {
                var text = Text(heading.Level <= 2 ? "helpHeading" : "helpSubheading");
                AddSpans(text, heading.Spans);
                AutomationProperties.SetHeadingLevel(text, heading.Level);
                text.Focusable = true;
                if (anchors.TryDequeue(out var named))
                {
                    _headings[named.Anchor] = text;
                }

                return text;
            }

            case HelpParagraphBlock paragraph:
            {
                var text = Text("helpBody");
                AddSpans(text, paragraph.Spans);
                return text;
            }

            case HelpListBlock list:
                return List(list, anchors);

            case HelpCodeBlock code:
            {
                var text = Text("helpCodeText");
                text.Inlines!.Add(new Run(code.Text));
                return new Border { Classes = { "helpCode" }, Child = text };
            }

            case HelpTableBlock table:
                return Table(table);

            case HelpQuoteBlock quote:
            {
                var panel = new StackPanel();
                foreach (var inner in quote.Blocks)
                {
                    panel.Children.Add(Draw(inner, anchors));
                }

                return new Border { Classes = { "helpQuote" }, Child = panel };
            }

            case HelpImageBlock image:
                return Picture(image);

            default:
                return new Border { Classes = { "helpRule" } };
        }
    }

    private static SelectableTextBlock Text(string style)
    {
        var text = new SelectableTextBlock { Classes = { style }, TextWrapping = TextWrapping.Wrap, Inlines = new InlineCollection() };

        // Tab goes from link to link. Text can still be clicked into and selected.
        KeyboardNavigation.SetIsTabStop(text, false);
        return text;
    }

    private Control List(HelpListBlock list, Queue<HelpHeading> anchors)
    {
        var grid = new Grid { Classes = { "helpList" }, ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (var index = 0; index < list.Items.Count; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var marker = new TextBlock { Classes = { "helpMarker" }, Text = list.Start is { } start ? $"{start + index}." : "•" };
            Grid.SetRow(marker, index);
            grid.Children.Add(marker);

            var content = new StackPanel { Classes = { "helpItem" } };
            foreach (var block in list.Items[index])
            {
                content.Children.Add(Draw(block, anchors));
            }

            Grid.SetRow(content, index);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
        }

        return grid;
    }

    private Control Table(HelpTableBlock table)
    {
        var columns = table.Rows.Max(row => row.Count);
        var grid = new Grid();

        // Share the width by each column's longest cell, so a short one stays narrow.
        for (var column = 0; column < columns; column++)
        {
            var longest = table.Rows.Max(row => column < row.Count ? row[column].Sum(span => span.Text.Length) : 0);
            grid.ColumnDefinitions.Add(new ColumnDefinition(Math.Clamp(longest, 8, 48), GridUnitType.Star));
        }

        for (var row = 0; row < table.Rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < columns; column++)
            {
                var text = Text(row == 0 ? "helpCellHeading" : "helpCellText");
                AddSpans(text, column < table.Rows[row].Count ? table.Rows[row][column] : []);
                var cell = new Border { Classes = { "helpCell" }, Child = text };
                if (row == 0)
                {
                    cell.Classes.Add("header");
                }

                if (row == table.Rows.Count - 1)
                {
                    cell.Classes.Add("last");
                }

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return new Border { Classes = { "helpTable" }, Child = grid };
    }

    private Control Picture(HelpImageBlock image)
    {
        if (ResolveImage?.Invoke(image.Target) is { } found)
        {
            try
            {
                using var content = new MemoryStream(found.Content);
                var picture = new Image { Source = new Bitmap(content), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(picture, image.AltText);
                return new Border { Classes = { "helpImage" }, Child = picture };
            }
            catch (Exception)
            {
                // Not a picture this platform can draw: its description says what it showed.
            }
        }

        var text = Text("helpImageText");
        text.Inlines!.Add(new Run(image.AltText));
        return text;
    }

    private void AddSpans(SelectableTextBlock text, IReadOnlyList<HelpSpan> spans)
    {
        // Links are controls within the text, so its words are put together for a screen reader.
        AutomationProperties.SetName(text, string.Concat(spans.Select(span => span.Text)));
        foreach (var span in spans)
        {
            // One that goes nowhere, or that the app won't hand to the system, is only its words.
            var goes = span.Link is null ? null : ResolveLink?.Invoke(span.Link);
            if (goes is HelpPageLink or HelpExternalLink { Address.Scheme: "http" or "https" })
            {
                text.Inlines!.Add(LinkTo(span, span.Link!, goes));
                continue;
            }

            var run = new Run(span.Text);
            if (span.IsBold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }

            if (span.IsItalic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (span.IsCode && this.TryFindResource("MonoFont", out var font) && font is FontFamily mono)
            {
                run.FontFamily = mono;
            }

            text.Inlines!.Add(run);
        }
    }

    private InlineUIContainer LinkTo(HelpSpan span, string target, HelpLink goes)
    {
        var label = new TextBlock { Classes = { "helpLink" }, Text = span.Text, TextWrapping = TextWrapping.Wrap };
        if (span.IsBold)
        {
            label.FontWeight = FontWeight.SemiBold;
        }

        var button = new Button { Classes = { "link" }, Padding = default, MinHeight = 0, Content = label };
        var name = span.Text;
        if (goes is HelpExternalLink external)
        {
            ToolTip.SetTip(button, external.Address.AbsoluteUri);
            name += ", opens in your browser";
        }

        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => LinkActivated?.Invoke(this, target);
        return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.TextBottom };
    }
}

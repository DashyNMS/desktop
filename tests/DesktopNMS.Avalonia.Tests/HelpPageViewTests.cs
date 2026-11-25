using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.Core.Help;
using DesktopNMS.Views.Help;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

/// <summary>A help page drawn with the app's controls (#604): what a keyboard or a screen reader meets. A made-up page.</summary>
public sealed class HelpPageViewTests
{
    /// <summary>A 1x1 transparent PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static readonly HelpGuide Guide = HelpGuide.Load(HelpProduct.Desktop, null, new HelpFileSet(new Dictionary<string, byte[]>
    {
        ["docs/guide/README.md"] = Encoding.UTF8.GetBytes(
            "# Guide\n\nSee [the next page](next.md), [the site](https://example.net/a) and [gone](gone.md).\n\n## Pictures\n\n![A widget](../pic.png)\n\n![Not here](../none.png)\n\n- One\n\n| A | B |\n| --- | --- |\n| 1 | 2 |"),
        ["docs/guide/next.md"] = Encoding.UTF8.GetBytes("# Next\n\nText."),
        ["docs/pic.png"] = Png,
    }));

    private static (Window Window, HelpPageView View) Show()
    {
        var view = new HelpPageView();
        var page = Guide.Home!;
        view.ResolveLink = target => Guide.ResolveLink(page, target);
        view.ResolveImage = target => Guide.ResolveImage(page, target);
        view.Page = page;
        var window = new Window { Content = view, Width = 600, Height = 600 };
        window.Show();
        return (window, view);
    }

    [AvaloniaFact]
    public void Headings_are_headings_and_can_be_gone_to()
    {
        var (window, view) = Show();
        try
        {
            Assert.Equal(1, AutomationProperties.GetHeadingLevel(view.FindHeading(null)!));
            Assert.Equal(2, AutomationProperties.GetHeadingLevel(view.FindHeading("pictures")!));
            Assert.Same(view.FindHeading(null), view.FindHeading("nowhere"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Links_are_buttons_that_say_where_they_go_and_a_broken_one_is_only_text()
    {
        var (window, view) = Show();
        try
        {
            var chosen = new List<string>();
            view.LinkActivated += (_, target) => chosen.Add(target);
            var links = view.GetVisualDescendants().OfType<SelectableTextBlock>().SelectMany(text => text.Inlines!.OfType<InlineUIContainer>()).Select(inline => (Button)inline.Child).ToList();

            Assert.Equal(["the next page", "the site, opens in your browser"], links.Select(AutomationProperties.GetName));
            foreach (var link in links)
            {
                link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            Assert.Equal(["next.md", "https://example.net/a"], chosen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_picture_has_its_description_and_a_missing_one_is_its_description()
    {
        var (window, view) = Show();
        try
        {
            Assert.Equal("A widget", AutomationProperties.GetName(Assert.Single(view.GetVisualDescendants().OfType<Image>())));
            Assert.Contains(view.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Inlines!.OfType<Run>().Any(run => run.Text == "Not here"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Another_page_replaces_the_first()
    {
        var (window, view) = Show();
        try
        {
            view.Page = Guide.Find("Next");

            Assert.Null(view.FindHeading("pictures") is { } heading && heading != view.FindHeading(null) ? heading : null);
            Assert.Empty(view.GetVisualDescendants().OfType<Image>());
        }
        finally
        {
            window.Close();
        }
    }
}

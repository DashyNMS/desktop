using System.Net;
using System.Reflection;
using System.Text;
using DesktopNMS.Core.Help;
using DesktopNMS.Services;
using DesktopNMS.Services.Help;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Moving about Help (#604). The small guide here is made up; the bundled one is checked on its own.</summary>
public sealed class HelpViewModelTests : IDisposable
{
    private readonly Windows _windows;
    private readonly HelpGuideService _guides = Guides();
    private readonly HelpViewModel _help;

    public HelpViewModelTests()
    {
        var proxy = DispatchProxy.Create<IWindowService, Windows>();
        _windows = (Windows)(object)proxy;
        _help = new HelpViewModel(_guides, proxy, mayRefresh: false);
    }

    public void Dispose()
    {
        _help.Dispose();
        _guides.Dispose();
    }

    private static HelpGuideService Guides(HttpMessageHandler? handler = null) => new(HelpProduct.Desktop, HelpRelease.ForVersion("2.0.0-nightly.9"),
        new HelpFileSet(new Dictionary<string, string>
        {
            ["docs/guide/README.md"] = "# Guide\n\n## Tasks\n\n- [Widgets](widgets.md)\n- [Fixes](fixes.md)",
            ["docs/guide/widgets.md"] = "# Widgets\n\nSee [fixes](fixes.md#stuck), [the site](https://example.net/a), [gone](gone.md).\n\n![Pic](../pic.png)\n\n## Remove\n\nUnpin it.",
            ["docs/guide/fixes.md"] = "# Fixes\n\n## Stuck\n\nRestart the gizmo.",
            ["docs/pic.png"] = "not really",
        }.Select(file => new KeyValuePair<string, byte[]>(file.Key, Encoding.UTF8.GetBytes(file.Value)))),
        http: handler is null ? null : new HttpClient(handler));

    [Fact]
    public void It_opens_on_the_front_page_and_says_which_release_it_describes()
    {
        _help.OnShown();

        Assert.Equal("Guide", _help.Title);
        Assert.Equal("Help for DashyNMS 2.0.0-nightly.9", _help.ReleaseText);
        Assert.Equal([null, "TASKS"], _help.Groups.Select(group => group.Heading));
        Assert.True(_help.Groups[0].Entries[0].IsSelected);
    }

    [Fact]
    public void Back_and_forward_follow_where_you_have_been()
    {
        var places = new List<string?>();
        _help.Navigated += (_, anchor) => places.Add(_help.Page!.Id + "#" + anchor);
        _help.OnShown();
        _help.Groups[1].Entries[0].OpenCommand.Execute(null);
        _help.Activate("fixes.md#stuck");
        Assert.False(_help.ForwardCommand.CanExecute(null));

        _help.BackCommand.Execute(null);
        _help.BackCommand.Execute(null);
        Assert.False(_help.BackCommand.CanExecute(null));
        _help.ForwardCommand.Execute(null);
        Assert.Equal(["Home#", "Widgets#", "Fixes#stuck", "Widgets#", "Home#", "Widgets#"], places);

        // Somewhere new leaves Forward's pages behind.
        _help.Open("Widgets", "remove");
        Assert.False(_help.ForwardCommand.CanExecute(null));

        // The same place again isn't another step.
        _help.Open("Widgets", "remove");
        _help.BackCommand.Execute(null);
        Assert.Equal("Widgets#", places[^1]);
    }

    [Fact]
    public void Links_outside_the_guide_open_the_browser_and_broken_ones_do_nothing()
    {
        _help.Open("Widgets");

        _help.Activate("https://example.net/a");
        _help.Activate("gone.md");
        _help.Activate("file:///etc/passwd");

        Assert.Equal([new Uri("https://example.net/a")], _windows.Opened);
        Assert.Equal("Widgets", _help.Page!.Id);
        Assert.NotNull(_help.Image("../pic.png"));
        Assert.Null(_help.Image("https://example.net/pic.png"));
    }

    [Fact]
    public void The_page_on_the_web_is_the_one_showing()
    {
        _help.Open("Fixes");
        _help.OpenOnWebCommand.Execute(null);

        Assert.Equal([new Uri("https://github.com/DashyNMS/desktop/wiki/Fixes")], _windows.Opened);
    }

    [Fact]
    public void Search_finds_pages_and_opens_one()
    {
        _help.OnShown();
        _help.SearchText = "gizmo";

        Assert.True(_help.IsSearching);
        Assert.Equal("1 match", _help.ResultsText);
        var result = Assert.Single(_help.Results);
        Assert.Equal("Fixes, Stuck", result.AccessibleName);
        result.OpenCommand.Execute(null);
        Assert.Equal("Fixes", _help.Page!.Id);
        Assert.False(_help.IsSearching);

        _help.SearchText = "espresso";
        Assert.True(_help.ShowNoResults);
        _help.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, _help.ResultsText);
    }

    [Fact]
    public void A_page_the_guide_does_not_have_opens_its_front_page()
    {
        _help.Open("Nowhere", "at-all");

        Assert.Equal("Home", _help.Page!.Id);
    }

    [Fact]
    public async Task Pages_are_refreshed_once_and_only_where_that_is_allowed()
    {
        var server = new NotThere();
        using var guides = Guides(server);
        using var help = new HelpViewModel(guides, (IWindowService)(object)_windows);
        help.OnShown();
        help.OnShown();
        for (var wait = 0; wait < 200 && server.Requests == 0; wait++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, server.Requests);

        var demo = new NotThere();
        using var demoGuides = Guides(demo);
        using var inDemo = new HelpViewModel(demoGuides, (IWindowService)(object)_windows, mayRefresh: false);
        inDemo.OnShown();
        await Task.Delay(100);
        Assert.Equal(0, demo.Requests);
    }

    /// <summary>The guide that ships: every page is listed, and every link and image on every page goes somewhere.</summary>
    [Fact]
    public void The_bundled_guide_is_whole()
    {
        var guide = HelpGuide.Load(HelpProduct.Desktop, HelpRelease.ForVersion("2.0.0"), BundledHelp.Files);

        Assert.True(guide.Pages.Count > 15);
        Assert.DoesNotContain(guide.Contents, group => group.Name == "More");
        foreach (var page in guide.Pages)
        {
            foreach (var block in All(HelpBlocks.Read(page.Markdown)))
            {
                if (block is HelpImageBlock image)
                {
                    Assert.True(guide.ResolveImage(page, image.Target) is not null, $"{page.Path}: {image.Target}");
                    Assert.False(string.IsNullOrWhiteSpace(image.AltText), $"{page.Path}: {image.Target} has no description");
                }

                foreach (var link in Spans(block).Select(span => span.Link).OfType<string>().Distinct())
                {
                    Assert.False(guide.ResolveLink(page, link) is HelpBrokenLink, $"{page.Path}: {link}");
                }
            }
        }
    }

    [Fact]
    public void Every_part_of_the_app_has_a_page_the_guide_really_has()
    {
        var guide = HelpGuide.Load(HelpProduct.Desktop, null, BundledHelp.Files);

        foreach (var topic in HelpTopics.All)
        {
            var page = guide.Find(topic.PageId);
            Assert.True(page is not null, topic.PageId);
            Assert.True(topic.Anchor is null || page!.FindHeading(topic.Anchor) is not null, $"{topic.PageId}#{topic.Anchor}");
        }
    }

    [Theory]
    [InlineData("https://github.com/DashyNMS/desktop/wiki/Build-your-dashboard#keep-a-sensor-in-view", "Build-your-dashboard", "keep-a-sensor-in-view")]
    [InlineData("https://github.com/DashyNMS/desktop/wiki/Troubleshooting", "Troubleshooting", null)]
    [InlineData("https://github.com/DashyNMS/mobile/wiki/Troubleshooting", null, null)]
    [InlineData("https://github.com/DashyNMS/desktop/wiki/", null, null)]
    [InlineData("https://example.net/DashyNMS/desktop/wiki/Troubleshooting", null, null)]
    [InlineData("http://github.com/DashyNMS/desktop/wiki/Troubleshooting", null, null)]
    public void Only_this_guides_own_address_opens_in_the_app(string address, string? page, string? anchor)
        => Assert.Equal(page is null ? null : new HelpTopic(page, anchor), HelpTopics.FromAddress(new Uri(address)));

    private static IEnumerable<HelpBlock> All(IEnumerable<HelpBlock> blocks) => blocks.SelectMany(block => block switch
    {
        HelpListBlock list => All(list.Items.SelectMany(item => item)).Prepend(block),
        HelpQuoteBlock quote => All(quote.Blocks).Prepend(block),
        _ => [block],
    });

    private static IEnumerable<HelpSpan> Spans(HelpBlock block) => block switch
    {
        HelpHeadingBlock heading => heading.Spans,
        HelpParagraphBlock paragraph => paragraph.Spans,
        HelpTableBlock table => table.Rows.SelectMany(row => row).SelectMany(cell => cell),
        _ => [],
    };

    /// <summary>Records what's opened in the browser.</summary>
    public class Windows : DispatchProxy
    {
        public List<Uri> Opened { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWindowService.OpenUrl))
            {
                Opened.Add((Uri)args![0]!);
            }

            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    private sealed class NotThere : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }
}

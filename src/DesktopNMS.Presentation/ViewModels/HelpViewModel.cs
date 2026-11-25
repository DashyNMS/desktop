using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Help;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.Services.Help;

namespace DesktopNMS.ViewModels;

/// <summary>A page in the contents list.</summary>
public sealed class HelpEntryViewModel(HelpContentsEntry entry, Action<string> open) : ObservableObject
{
    private bool _isSelected;

    public string PageId => entry.PageId;

    public string Title => entry.Title;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand OpenCommand { get; } = new(() => open(entry.PageId));
}

/// <summary>A group in the contents list. The first has no name.</summary>
public sealed record HelpGroupViewModel(string? Name, IReadOnlyList<HelpEntryViewModel> Entries)
{
    public bool HasName => Name is not null;

    public string? Heading => Name?.ToUpperInvariant();
}

/// <summary>A search result: a page, or a heading on one.</summary>
public sealed class HelpResultViewModel(HelpSearchResult result, Action<string, string?> open)
{
    public string Title => result.Page.Title;

    public string? Heading => result.Heading?.Text;

    public bool HasHeading => Heading is not null;

    public string Passage => result.Passage;

    public string AccessibleName => Heading is null ? Title : Title + ", " + Heading;

    public RelayCommand OpenCommand { get; } = new(() => open(result.Page.Id, result.Heading?.Anchor));
}

/// <summary>
/// Help (#604): the user guide for the version that's running, read inside the
/// app. The main window has one as a page; a window of its own shows another
/// where the main window can't be used, such as while signing in.
/// </summary>
public sealed class HelpViewModel : ObservableObject, IDisposable
{
    /// <summary>More than anyone steps back through.</summary>
    private const int MaxHistory = 100;

    private const int MaxResults = 20;

    private readonly HelpGuideService _guides;
    private readonly IWindowService _windows;
    private readonly bool _mayRefresh;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<HelpTopic> _history = [];
    private int _at = -1;
    private bool _refreshed;
    private bool _disposed;
    private HelpGuide _guide;
    private HelpPage? _page;
    private string _searchText = string.Empty;
    private IReadOnlyList<HelpGroupViewModel> _groups = [];
    private IReadOnlyList<HelpResultViewModel> _results = [];

    /// <param name="mayRefresh">Whether pages may be refreshed from GitHub: not in the demo, which contacts nothing.</param>
    public HelpViewModel(HelpGuideService guides, IWindowService windows, bool mayRefresh = true)
    {
        _guides = guides;
        _windows = windows;
        _mayRefresh = mayRefresh;
        _dispatcher = UiThread.Dispatcher;
        _guide = guides.Current;
        BackCommand = new RelayCommand(() => Step(-1), () => _at > 0);
        ForwardCommand = new RelayCommand(() => Step(1), () => _at < _history.Count - 1);
        HomeCommand = new RelayCommand(() => Open(HelpTopics.Home));
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        OpenOnWebCommand = new RelayCommand(
            () => _windows.OpenUrl(HelpProduct.Desktop.WikiAddress(_page?.Id ?? HelpGuide.HomeId)),
            () => _page is not null);
        LoadContents();
        _guides.Changed += OnGuideChanged;
    }

    /// <summary>A page is showing: go to this heading, or the top when null.</summary>
    public event EventHandler<string?>? Navigated;

    /// <summary>Ctrl+F on the page.</summary>
    public event EventHandler? SearchFocusRequested;

    public RelayCommand BackCommand { get; }

    public RelayCommand ForwardCommand { get; }

    public RelayCommand HomeCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public RelayCommand OpenOnWebCommand { get; }

    public IReadOnlyList<HelpGroupViewModel> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    /// <summary>The page showing, or null when the guide couldn't be read at all.</summary>
    public HelpPage? Page
    {
        get => _page;
        private set
        {
            if (SetProperty(ref _page, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(HasPage));
            }
        }
    }

    public bool HasPage => Page is not null;

    public string Title => Page?.Title ?? "Help";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                Results = IsSearching ? Search(_searchText) : [];
                OnPropertyChanged(nameof(IsSearching));
                OnPropertyChanged(nameof(ShowNoResults));
                OnPropertyChanged(nameof(ResultsText));
            }
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public IReadOnlyList<HelpResultViewModel> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }

    public bool ShowNoResults => IsSearching && Results.Count == 0;

    /// <summary>Said by a screen reader as the results change.</summary>
    public string ResultsText => !IsSearching ? string.Empty : Results.Count switch
    {
        0 => "Nothing in the guide matches.",
        1 => "1 match",
        var count => $"{count} matches",
    };

    /// <summary>Which version the help describes, and when it was refreshed if it has been.</summary>
    public string ReleaseText
        => (_guide.Release is { } release ? $"Help for DashyNMS {release.Version}" : "Help for this build of DashyNMS")
           + (_guides is { Source: HelpGuideSource.Downloaded, TakenAt: { } taken } ? $" · updated {taken.ToLocalTime():d MMMM yyyy}" : string.Empty);

    /// <summary>The page is on screen. The first time, newer pages for this version are looked for in the background.</summary>
    public void OnShown()
    {
        if (Page is null)
        {
            Open(HelpTopics.Home);
        }

        if (_mayRefresh && !_refreshed)
        {
            _refreshed = true;
            _ = RefreshAsync();
        }
    }

    public void RequestSearchFocus() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    public void Open(HelpTopic topic) => Open(topic.PageId, topic.Anchor);

    /// <summary>Opens a page, remembering where Back goes. A page the guide doesn't have opens its front page.</summary>
    public void Open(string pageId, string? anchor = null)
    {
        var page = _guide.Find(pageId) ?? _guide.Home;
        if (page is null)
        {
            return;
        }

        SearchText = string.Empty;
        var topic = new HelpTopic(page.Id, page.FindHeading(anchor)?.Anchor);
        if (_at < 0 || _history[_at] != topic)
        {
            // Going somewhere new leaves what Forward had behind.
            _history.RemoveRange(_at + 1, _history.Count - _at - 1);
            _history.Add(topic);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }

            _at = _history.Count - 1;
        }

        Show(topic);
    }

    /// <summary>A link in the page was chosen: another page, the browser, or nothing for one that goes nowhere.</summary>
    public void Activate(string target)
    {
        switch (Resolve(target))
        {
            case HelpPageLink link:
                Open(link.PageId, link.Anchor);
                break;
            case HelpExternalLink link:
                _windows.OpenUrl(link.Address);
                break;
        }
    }

    /// <summary>Where a link in the page goes, for how it's shown and what its tooltip says.</summary>
    public HelpLink Resolve(string target) => Page is { } page ? _guide.ResolveLink(page, target) : new HelpBrokenLink(target);

    /// <summary>An image in the page, or null to show its description instead.</summary>
    public HelpImage? Image(string target) => Page is { } page ? _guide.ResolveImage(page, target) : null;

    private void Step(int by)
    {
        _at += by;
        SearchText = string.Empty;
        Show(_history[_at]);
    }

    private void Show(HelpTopic topic)
    {
        Page = _guide.Find(topic.PageId) ?? _guide.Home;
        foreach (var entry in Groups.SelectMany(group => group.Entries))
        {
            entry.IsSelected = entry.PageId == Page?.Id;
        }

        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
        OpenOnWebCommand.RaiseCanExecuteChanged();
        Navigated?.Invoke(this, topic.Anchor);
    }

    private IReadOnlyList<HelpResultViewModel> Search(string text)
        => _guide.Search(text, MaxResults).Select(result => new HelpResultViewModel(result, (page, anchor) => Open(page, anchor))).ToList();

    private void LoadContents()
        => Groups = _guide.Contents
            .Select(group => new HelpGroupViewModel(group.Name, group.Entries.Select(entry => new HelpEntryViewModel(entry, page => Open(page))).ToList()))
            .ToList();

    private async Task RefreshAsync()
    {
        try
        {
            await _guides.RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Also when the pages are the same: they're now known to be.
        _dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                OnPropertyChanged(nameof(ReleaseText));
            }
        });
    }

    /// <summary>On the thread that refreshed it. The page stays where it is, with its new text.</summary>
    private void OnGuideChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        _guide = _guides.Current;
        LoadContents();
        if (_at >= 0)
        {
            Show(_history[_at]);
        }

        if (IsSearching)
        {
            Results = Search(SearchText);
            OnPropertyChanged(nameof(ShowNoResults));
            OnPropertyChanged(nameof(ResultsText));
        }

        OnPropertyChanged(nameof(ReleaseText));
    });

    public void Dispose()
    {
        _disposed = true;
        _guides.Changed -= OnGuideChanged;
    }
}

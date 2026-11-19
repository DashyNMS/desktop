using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Search;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The Network Search page (#57): one search box that finds an endpoint by
/// IP address, network, MAC address or name and shows where it is connected,
/// on Core's shared lookup (core#91) - LibreNMS 26.11's Network Search page
/// (librenms#21113, librenms#21159) in the desktop's own layout.
/// </summary>
/// <remarks>
/// <para>
/// Where LibreNMS lists every port that learned the MAC and leaves the reader
/// to work out which one matters, this leads with the answer - the port the
/// endpoint most likely plugs into, and how sure that is - and keeps uplinks
/// as context. It also says what it couldn't check: a source that was denied,
/// unavailable or failed is shown as such, never folded into "not found".
/// </para>
/// <para>
/// A search started while another is running cancels it; a result from a
/// search the user has moved on from, or from a connection that has since
/// gone, is dropped rather than shown.
/// </para>
/// </remarks>
public sealed class NetworkSearchViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ISessionService _session;
    private readonly ILogger<NetworkSearchViewModel> _logger;
    private CancellationTokenSource? _searchCts;
    private int _generation;

    private string _query = string.Empty;
    private bool _isBusy;
    private string? _errorMessage;
    private NetworkSearchResult? _result;
    private EndpointViewModel? _endpoint;
    private string _rowFilter = string.Empty;
    private DateTimeOffset? _searchedAt;
    private MacAddressFormat _macFormat;

    public NetworkSearchViewModel(
        ILibreNmsClient client,
        ISettingsStore settings,
        IWindowService windows,
        ISessionService session,
        ILogger<NetworkSearchViewModel> logger)
    {
        _client = client;
        _settings = settings;
        _windows = windows;
        _session = session;
        _logger = logger;

        _macFormat = _settings.Current.NetworkSearchMacFormatValue;
        _settings.Changed += OnSettingsChanged;

        Recent = new ObservableCollection<string>(_settings.Current.NetworkSearchRecent);
        Rows = new BatchObservableCollection<EndpointRowViewModel>();
        SourceFilter = new FilterFacet(OnFiltersChanged);
        SeenByFilter = new FilterFacet(OnFiltersChanged);
        MonitoredFilter = new FilterFacet(OnFiltersChanged);
        RowsView = new FilteredView(Rows) { Filter = o => o is EndpointRowViewModel row && Shows(row) };
        ShowFiltersCommand = new RelayCommand(() => _windows.ShowNetworkSearchFiltersDialog(), () => IsList);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        Coverage = new BatchObservableCollection<CoverageItemViewModel>();

        SearchCommand = new AsyncRelayCommand(() => SearchAsync(Query, 0));
        UseRecentCommand = new AsyncRelayCommand(p => p is string q ? SearchAsync(q, 0) : Task.CompletedTask);
        ClearRecentCommand = new RelayCommand(ClearRecent, () => Recent.Count > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        NextPageCommand = new AsyncRelayCommand(() => SearchAsync(_result!.Query.Text, _result.Offset + _result.Endpoints.Count, remember: false), () => _result?.Remaining > 0 && !IsBusy);
        PreviousPageCommand = new AsyncRelayCommand(() => SearchAsync(_result!.Query.Text, Math.Max(0, _result.Offset - PageSize), remember: false), () => _result?.Offset > 0 && !IsBusy);
        CopyCsvCommand = new RelayCommand(() => UiPlatform.Clipboard.SetText(Csv()), () => HasResults);
        SaveCsvCommand = new RelayCommand(SaveCsv, () => HasResults);

        _session.StateChanged += OnSessionStateChanged;
    }

    /// <summary>How many endpoints a list shows per page - Core's default.</summary>
    public int PageSize { get; } = new NetworkSearchOptions().PageSize;

    /// <summary>The search box. Several addresses, MACs or hostnames can be pasted at once.</summary>
    public string Query
    {
        get => _query;
        set => SetProperty(ref _query, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
                NextPageCommand.RaiseCanExecuteChanged();
                PreviousPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Newest first, as Settings remembers them.</summary>
    public ObservableCollection<string> Recent { get; }

    public bool HasRecent => Recent.Count > 0;

    /// <summary>
    /// Filters a list's rows the way LibreNMS's Source and network filters do:
    /// tick the values to show (any of them), untick one to hide it.
    /// </summary>
    public FilterFacet SourceFilter { get; }

    /// <summary>Filters by the router whose ARP table holds the endpoint.</summary>
    public FilterFacet SeenByFilter { get; }

    /// <summary>Monitored devices and interfaces, or everything else.</summary>
    public FilterFacet MonitoredFilter { get; }

    public bool IsFiltersActive => SourceFilter.HasActiveFilter || SeenByFilter.HasActiveFilter || MonitoredFilter.HasActiveFilter;

    public RelayCommand ShowFiltersCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>"12 of 25 shown" when filters hide rows.</summary>
    public string? FilteredText => IsList && (IsFiltersActive || RowFilter.Length > 0)
        ? $"{RowsView.Cast<object>().Count().ToString(CultureInfo.CurrentCulture)} of {Rows.Count.ToString(CultureInfo.CurrentCulture)} on this page shown"
        : null;

    /// <summary>The status bar's left side: when the results on screen were fetched.</summary>
    public string? SearchedText => _searchedAt is { } at ? "searched " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) : null;

    /// <summary>The single endpoint the query identified, when it identified one.</summary>
    public EndpointViewModel? Endpoint
    {
        get => _endpoint;
        private set => SetProperty(ref _endpoint, value);
    }

    /// <summary>A list's rows: several matches, or the overview.</summary>
    public BatchObservableCollection<EndpointRowViewModel> Rows { get; }

    public FilteredView RowsView { get; }

    /// <summary>Narrows the current page by any text in a row.</summary>
    public string RowFilter
    {
        get => _rowFilter;
        set
        {
            if (SetProperty(ref _rowFilter, value ?? string.Empty))
            {
                RowsView.Refresh();
                OnPropertyChanged(nameof(FilteredText));
            }
        }
    }

    /// <summary>What each source said, in plain words.</summary>
    public BatchObservableCollection<CoverageItemViewModel> Coverage { get; }

    public bool IsIdle => _result is null && !IsBusy;

    public bool IsSingle => _result?.Outcome == NetworkSearchOutcome.Single && Endpoint is not null;

    public bool IsList => _result?.Outcome is NetworkSearchOutcome.List or NetworkSearchOutcome.Overview;

    public bool IsNotFound => _result?.Outcome == NetworkSearchOutcome.NotFound;

    public bool HasResults => _result is { Endpoints.Count: > 0 };

    public bool HasCoverage => Coverage.Count > 0;

    /// <summary>"Showing 1–25 of 64 endpoints" - or the overview's equivalent.</summary>
    public string? ListHeading => _result is not { } r || !IsList ? null
        : r.Outcome == NetworkSearchOutcome.Overview
            ? $"Known endpoints: {Range(r)} of {r.Total.ToString("N0", CultureInfo.CurrentCulture)}, from routers' ARP tables"
            : $"{r.Total.ToString("N0", CultureInfo.CurrentCulture)} endpoints match — showing {Range(r)}. Open one to see where it is connected.";

    public bool HasMorePages => _result is { } r && (r.Remaining > 0 || r.Offset > 0);

    /// <summary>
    /// Nothing found, said as precisely as the coverage allows: a confirmed
    /// absence only when every relevant source answered.
    /// </summary>
    public string NotFoundTitle => _result?.IsConfirmedAbsent == true
        ? "Not on the network"
        : "Nothing found in the sources that could be checked";

    public string NotFoundDetail => _result?.IsConfirmedAbsent == true
        ? $"No router, switch or monitored device knows {QueryShown}. It may be offline or outside what LibreNMS polls."
        : $"Some sources couldn't be read, so {QueryShown} may still be on the network. See what was checked below.";

    /// <summary>Pasted terms past the limit, which weren't searched.</summary>
    public string? IgnoredTermsText => _result?.Query.IgnoredTerms is > 0 and var n
        ? $"Only the first {NetworkSearchQuery.MaxTerms} pasted terms were searched; {n} more were left out."
        : null;

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand UseRecentCommand { get; }

    public RelayCommand ClearRecentCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand NextPageCommand { get; }

    public AsyncRelayCommand PreviousPageCommand { get; }

    public RelayCommand CopyCsvCommand { get; }

    public RelayCommand SaveCsvCommand { get; }

    /// <summary>Raised when something outside the page (the keyboard shortcut) wants the search box focused.</summary>
    public event EventHandler? FocusSearchRequested;

    public void RequestFocus() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Searches for <paramref name="query"/> - from another page ("find this MAC") as well as the box.</summary>
    public Task SearchForAsync(string query)
    {
        Query = query;
        return SearchAsync(query, 0);
    }

    private string QueryShown => _result is { Query.Text: { Length: > 0 } text } ? $"“{text}”" : "it";

    private async Task SearchAsync(string query, int offset, bool remember = true)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = _searchCts = new CancellationTokenSource();
        var generation = ++_generation;

        Query = query;
        ErrorMessage = null;
        IsBusy = true;
        OnPropertyChanged(nameof(IsIdle));

        try
        {
            var result = await new NetworkSearch(_client).SearchAsync(query, offset, cts.Token).ConfigureAwait(true);
            if (generation != _generation || !_session.IsConnected)
            {
                return;
            }

            if (remember && !result.Query.IsEmpty)
            {
                Remember(result.Query.Text);
            }

            _searchedAt = DateTimeOffset.Now;
            Present(result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Replaced by a newer search, cancelled, or the connection went.
        }
        catch (LibreNmsApiException ex) when (generation == _generation)
        {
            _logger.LogWarning("Network search failed: {Message}", ex.Message);
            ErrorMessage = ex.ToUserMessage();
        }
        catch (Exception ex) when (generation == _generation && ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Network search failed unexpectedly");
            ErrorMessage = "The search couldn't finish. Try again, or check the connection to LibreNMS.";
        }
        finally
        {
            if (generation == _generation)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    private void Present(NetworkSearchResult? result)
    {
        _result = result;
        var format = _macFormat;

        Endpoint = result is { Outcome: NetworkSearchOutcome.Single, Endpoints: [var only] }
            ? new EndpointViewModel(only, format, _windows, _settings.Current.ServerTimestampsAreUtc, SearchForAsync)
            : null;

        using (Rows.BeginBatch())
        {
            Rows.Clear();
            if (result is { Outcome: NetworkSearchOutcome.List or NetworkSearchOutcome.Overview })
            {
                foreach (var endpoint in result.Endpoints)
                {
                    Rows.Add(new EndpointRowViewModel(endpoint, format, _windows, SearchForAsync));
                }
            }
        }

        SourceFilter.Apply(Counts(Rows.SelectMany(r => r.SourceKeys)));
        SeenByFilter.Apply(Counts(Rows.SelectMany(r => r.SeenByKeys)));
        MonitoredFilter.Apply(Counts(Rows.Select(r => r.MonitoredKey)));
        RowsView.Refresh();

        using (Coverage.BeginBatch())
        {
            Coverage.Clear();
            foreach (var source in result?.Coverage ?? [])
            {
                Coverage.Add(new CoverageItemViewModel(source));
            }
        }

        foreach (var name in new[]
                 {
                     nameof(IsSingle), nameof(IsList), nameof(IsNotFound), nameof(IsIdle), nameof(HasResults), nameof(HasCoverage),
                     nameof(ListHeading), nameof(HasMorePages), nameof(NotFoundTitle), nameof(NotFoundDetail), nameof(IgnoredTermsText),
                     nameof(IsFiltersActive), nameof(FilteredText), nameof(SearchedText),
                 })
        {
            OnPropertyChanged(name);
        }

        NextPageCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
        CopyCsvCommand.RaiseCanExecuteChanged();
        SaveCsvCommand.RaiseCanExecuteChanged();
        ShowFiltersCommand.RaiseCanExecuteChanged();
    }

    private static IEnumerable<(string Key, string DisplayText, int Count)> Counts(IEnumerable<string> keys)
        => keys.GroupBy(k => k).Select(g => (g.Key, g.Key, g.Count()));

    /// <summary>A row passes the text filter and every facet - a ticked value in each facet it has values for.</summary>
    private bool Shows(EndpointRowViewModel row)
        => row.Matches(RowFilter)
           && SourceFilter.AllowsAny(row.SourceKeys)
           && SeenByFilter.AllowsAny(row.SeenByKeys)
           && MonitoredFilter.Allows(row.MonitoredKey);

    private void OnFiltersChanged()
    {
        RowsView.Refresh();
        OnPropertyChanged(nameof(IsFiltersActive));
        OnPropertyChanged(nameof(FilteredText));
    }

    private void ClearFilters()
    {
        foreach (var facet in new[] { SourceFilter, SeenByFilter, MonitoredFilter })
        {
            facet.CheckAllCommand.Execute(null);
        }

        RowFilter = string.Empty;
    }

    /// <summary>Settings › Devices › MAC addresses: rewrite what's on screen in the new format.</summary>
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var format = settings.NetworkSearchMacFormatValue;
        if (format == _macFormat)
        {
            return;
        }

        UiThread.Dispatcher.Post(() =>
        {
            _macFormat = format;
            Present(_result);
        });
    }

    private void Remember(string query)
    {
        _settings.Current.RememberNetworkSearch(query);
        _settings.SaveQuietly();
        Recent.Clear();
        foreach (var q in _settings.Current.NetworkSearchRecent)
        {
            Recent.Add(q);
        }

        OnPropertyChanged(nameof(HasRecent));
        ClearRecentCommand.RaiseCanExecuteChanged();
    }

    private void ClearRecent()
    {
        _settings.Current.NetworkSearchRecent.Clear();
        _settings.SaveQuietly();
        Recent.Clear();
        OnPropertyChanged(nameof(HasRecent));
        ClearRecentCommand.RaiseCanExecuteChanged();
    }

    private void Cancel()
    {
        _generation++;
        _searchCts?.Cancel();
        IsBusy = false;
        OnPropertyChanged(nameof(IsIdle));
    }

    private string Csv() => _result is { } r ? NetworkSearchExport.ToCsv(r.Endpoints, _macFormat) : string.Empty;

    private void SaveCsv()
    {
        var path = UiPlatform.FileDialogs.PickFileToSave(new FileDialogOptions(
            Filter: "CSV files (*.csv)|*.csv",
            FileName: $"network-search-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv"));
        if (path is not null)
        {
            File.WriteAllText(path, Csv());
        }
    }

    /// <summary>A result from a connection that has gone is another account's data: drop it (#593).</summary>
    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (_session.IsConnected)
        {
            return;
        }

        UiThread.Dispatcher.Post(() =>
        {
            Cancel();
            ErrorMessage = null;
            Present(null);
            Recent.Clear();
            OnPropertyChanged(nameof(HasRecent));
        });
    }

    private static string Range(NetworkSearchResult r) => r.Endpoints.Count == 0
        ? "none"
        : $"{(r.Offset + 1).ToString("N0", CultureInfo.CurrentCulture)}–{(r.Offset + r.Endpoints.Count).ToString("N0", CultureInfo.CurrentCulture)}";
}

/// <summary>One choice in Settings › Devices › MAC addresses - shown as an example address, as LibreNMS does.</summary>
public sealed record MacFormatOption(MacAddressFormat Format, string Example)
{
    public static IReadOnlyList<MacFormatOption> All { get; } =
    [
        new(MacAddressFormat.Colon, "aa:bb:cc:dd:ee:ff"),
        new(MacAddressFormat.Hyphen, "aa-bb-cc-dd-ee-ff"),
        new(MacAddressFormat.Dotted, "aabb.ccdd.eeff"),
        new(MacAddressFormat.Bare, "aabbccddeeff"),
    ];

    public override string ToString() => Example;
}

/// <summary>One source's line in "What was checked".</summary>
public sealed class CoverageItemViewModel
{
    public CoverageItemViewModel(SourceCoverage coverage)
    {
        Label = coverage.Source switch
        {
            SearchSource.Arp => "ARP tables",
            SearchSource.NeighbourDiscovery => "IPv6 neighbours",
            SearchSource.ForwardingTable => "Switch MAC tables",
            SearchSource.Networks => "Interface addresses",
            SearchSource.Devices => "Devices",
            SearchSource.Links => "Neighbour links",
            SearchSource.Vendors => "MAC vendors",
            _ => coverage.Source.ToString(),
        };

        StateText = coverage.State switch
        {
            SourceState.Found when coverage.IsTruncated => $"found, {coverage.Shown} of {coverage.Total} checked",
            SourceState.Found => "found",
            SourceState.NoEntry => "nothing",
            SourceState.NotQueried => "not checked",
            SourceState.PermissionDenied => "no permission",
            SourceState.Unavailable => "unavailable",
            SourceState.Failed => "failed",
            _ => coverage.State.ToString(),
        };

        Tone = coverage.State switch
        {
            SourceState.Found when coverage.IsTruncated => AlertSeverity.Warning,
            SourceState.Found => AlertSeverity.Ok,
            SourceState.PermissionDenied or SourceState.Failed => AlertSeverity.Critical,
            SourceState.Unavailable => AlertSeverity.Warning,
            _ => AlertSeverity.Unknown,
        };

        Detail = coverage.Detail ?? Label + ": " + StateText;
        IsBlindSpot = coverage.State is SourceState.PermissionDenied or SourceState.Unavailable or SourceState.Failed || coverage.IsTruncated;
    }

    public string Label { get; }

    public string StateText { get; }

    public AlertSeverity Tone { get; }

    public string Detail { get; }

    /// <summary>Whether this source leaves a gap in what the result can claim.</summary>
    public bool IsBlindSpot { get; }

    public string AccessibleText => $"{Label}: {StateText}";
}

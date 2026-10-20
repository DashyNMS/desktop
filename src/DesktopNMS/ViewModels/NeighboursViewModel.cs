using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The Neighbours tab (#55): the user's own views of what their switches
/// see over LLDP/CDP, filtered by rules on their LLDP details - each
/// defined by rules on what the neighbour announces (see
/// <see cref="NeighbourViewDefinition"/>), so no vendor is built in. A
/// view lists its neighbours with their switch port's state and traffic,
/// and the selected one's port graphs underneath.
/// </summary>
public sealed class NeighboursViewModel : ObservableObject, IDisposable
{
    private readonly INeighbourDirectory _directory;
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly IWindowService _windows;
    private readonly ILogger<NeighboursViewModel> _logger;

    private NeighbourSnapshot? _snapshot;
    private NeighbourViewDefinition? _selectedView;
    private bool _hasLoadedOnce;
    private bool _isBusy;
    private string? _errorMessage;
    private string _searchText = string.Empty;
    private bool _showUp = true;
    private bool _showDown = true;
    private bool _showOther = true;
    private NeighbourItemViewModel? _selected;
    private (string Name, string? Mac)? _pendingSelection;
    private readonly IUiTimer _autoRefreshTimer;
    private bool _isShowing;

    public NeighboursViewModel(
        INeighbourDirectory directory,
        ILibreNmsClient client,
        ISessionService session,
        ISettingsStore settings,
        IDeviceCache devices,
        IWindowService windows,
        ILogger<NeighboursViewModel> logger)
    {
        _directory = directory;
        _client = client;
        _session = session;
        _settings = settings;
        _devices = devices;
        _windows = windows;
        _logger = logger;

        Views = new ObservableCollection<NeighbourViewDefinition>();
        Items = new BatchObservableCollection<NeighbourItemViewModel>();
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is NeighbourItemViewModel n && IsStateShown(n.State) && n.Matches(SearchText);

        PortGraphs = new PortGraphsPanelViewModel(client, settings, logger);
        PortGraphs.OpenRequested += (_, request) => _windows.ShowDevicePortGraph(request.DeviceId, request.IfName, request.GraphType);
        PortGraphs.AddToDashboardRequested += (_, request) => _windows.AddPortGraphToDashboard(request.DeviceId, request.IfName, request.GraphType, request.Title);

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(refresh: true), () => _session.IsConnected && !IsBusy);
        ClearFiltersCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            ShowUp = ShowDown = ShowOther = true;
        });
        NewViewCommand = new RelayCommand(NewView);

        // From a row in the views list, or (no parameter) the open view.
        EditViewCommand = new RelayCommand(parameter => EditView(ViewFrom(parameter)));
        DeleteViewCommand = new RelayCommand(parameter => DeleteView(ViewFrom(parameter)));
        SelectViewCommand = new RelayCommand(parameter =>
        {
            // The All chip has no definition: back to every neighbour.
            if (parameter is NeighbourhoodChipViewModel { Definition: null })
            {
                ShowAll();
            }
            else if (ViewFrom(parameter) is { } view)
            {
                SelectedView = Views.FirstOrDefault(v => v.Id == view.Id);
            }
        });
        ToggleShowOnMapCommand = new RelayCommand(parameter => ToggleShowOnMap(ViewFrom(parameter)));
        MoveLeftCommand = new RelayCommand(parameter => Move(ViewFrom(parameter), -1));
        MoveRightCommand = new RelayCommand(parameter => Move(ViewFrom(parameter), +1));
        Neighbourhoods = new ObservableCollection<NeighbourhoodChipViewModel>();
        OpenSwitchCommand = new RelayCommand(parameter =>
        {
            if (parameter is NeighbourItemViewModel item)
            {
                _windows.ShowDeviceDetail(item.SwitchDeviceId);
            }
        });
        OpenDeviceCommand = new RelayCommand(parameter =>
        {
            if (parameter is NeighbourItemViewModel { Neighbour.RemoteDeviceId: { } deviceId })
            {
                _windows.ShowDeviceDetail(deviceId);
            }
        });

        // Refreshes at the Settings interval, only while the tab is showing (#291).
        _autoRefreshTimer = UiThread.CreateTimer(AutoRefreshInterval);
        _autoRefreshTimer.Tick += OnAutoRefreshTick;

        LoadViewsFromSettings();
        _settings.Changed += OnSettingsChanged;
    }

    private TimeSpan AutoRefreshInterval => TimeSpan.FromSeconds(Math.Max(15, _settings.Current.PollIntervalSeconds));

    /// <summary>The user's views, in the order they made them - also the Neighbours tab's hover menu.</summary>
    public ObservableCollection<NeighbourViewDefinition> Views { get; }

    public bool HasViews => Views.Count > 0;

    public NeighbourViewDefinition? SelectedView
    {
        get => _selectedView;
        set
        {
            if (SetProperty(ref _selectedView, value))
            {
                OnPropertyChanged(nameof(HasSelectedView));
                OnPropertyChanged(nameof(ViewRulesText));

                SelectedItem = null;
                RebuildItems();
                foreach (var chip in Neighbourhoods)
                {
                    chip.IsSelected = chip.Definition?.Id == value?.Id;
                }

                if (value is not null && _snapshot is null && _hasLoadedOnce)
                {
                    _ = LoadAsync(refresh: false);
                }
            }
        }
    }

    public bool HasSelectedView => _selectedView is not null;

    /// <summary>
    /// The chip row (#221): All, then each neighbourhood (a <see cref="NeighbourViewDefinition"/>,
    /// called a neighbourhood in the UI), each with how many neighbours it holds.
    /// </summary>
    public ObservableCollection<NeighbourhoodChipViewModel> Neighbourhoods { get; }

    /// <summary>Back to every neighbour - the "All" chip, and clicking the tab itself.</summary>
    public void ShowAll() => SelectedView = null;

    public RelayCommand ToggleShowOnMapCommand { get; }

    public RelayCommand MoveLeftCommand { get; }

    public RelayCommand MoveRightCommand { get; }

    /// <summary>"System description contains X and switch starts with Y" - what the selected view looks for.</summary>
    public string ViewRulesText => _selectedView is { } view ? NeighbourViewText.DescribeRules(view) : string.Empty;

    public BatchObservableCollection<NeighbourItemViewModel> Items { get; }

    public ICollectionView ItemsView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    public RelayCommand NewViewCommand { get; }

    public RelayCommand EditViewCommand { get; }

    public RelayCommand DeleteViewCommand { get; }

    /// <summary>The hover menu's items - picks a view by the definition it's given.</summary>
    public RelayCommand SelectViewCommand { get; }

    public RelayCommand OpenSwitchCommand { get; }

    /// <summary>Opens a neighbour's own Device Details, for one LibreNMS monitors.</summary>
    public RelayCommand OpenDeviceCommand { get; }

    /// <summary>Drives the loading/empty/no-matches split on the grid.</summary>
    public ListLoadState LoadState { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool HasAnyFilterApplied => !string.IsNullOrEmpty(SearchText) || !ShowUp || !ShowDown || !ShowOther;

    public bool ShowUp
    {
        get => _showUp;
        set => SetFilter(ref _showUp, value);
    }

    public bool ShowDown
    {
        get => _showDown;
        set => SetFilter(ref _showDown, value);
    }

    /// <summary>Shut-down ports, disabled or in-maintenance devices, and neighbours LibreNMS no longer sees.</summary>
    public bool ShowOther
    {
        get => _showOther;
        set => SetFilter(ref _showOther, value);
    }

    public int UpCount => Items.Count(i => i.State == NeighbourState.Up);

    public int DownCount => Items.Count(i => i.State == NeighbourState.Down);

    public int OtherCount => Items.Count(i => i.State == NeighbourState.Other);

    public bool HasOther => OtherCount > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
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

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public NeighbourItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                ShowPortGraphs();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>The selected neighbour's switch port graphs - traffic, packets and errors - side by side.</summary>
    public PortGraphsPanelViewModel PortGraphs { get; }

    private void ShowPortGraphs()
    {
        if (_selected is not { } item)
        {
            PortGraphs.Clear();
            return;
        }

        PortGraphs.Show(item.SwitchDeviceId, item.PortIfName, item.Name, $" - port {item.PortText} on {item.SwitchName}", "LibreNMS doesn't know this neighbour's switch port.");
    }

    /// <summary>
    /// Loads lazily the first time the tab is shown - same convention as
    /// every other tab - then keeps it up to date while it's showing (#291).
    /// Coming back to it after longer than the refresh interval refreshes
    /// straight away.
    /// </summary>
    public void OnShown()
    {
        _isShowing = true;
        _autoRefreshTimer.Start();

        if (!_hasLoadedOnce)
        {
            _hasLoadedOnce = true;
            _ = LoadAsync(refresh: false);
        }
        else if (_snapshot is { } snapshot && DateTimeOffset.Now - snapshot.LoadedAt >= AutoRefreshInterval)
        {
            RefreshInBackground();
        }
    }

    /// <summary>Another tab is showing, or the main window is hidden - stops refreshing until it's back.</summary>
    public void OnHidden()
    {
        _isShowing = false;
        _autoRefreshTimer.Stop();
    }

    private void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (_isShowing)
        {
            RefreshInBackground();
        }
    }

    private void RefreshInBackground()
    {
        if (_session.IsConnected && !IsBusy)
        {
            _ = LoadAsync(refresh: true, background: true);
        }
    }

    /// <summary>Opens a view on a neighbour, by name - the MAC picks out which of several unnamed ones.</summary>
    public void Show(string viewId, string? name, string? mac)
    {
        if (Views.FirstOrDefault(v => v.Id == viewId) is { } view)
        {
            SelectedView = view;
        }

        if (name is not null)
        {
            _pendingSelection = (name, mac);
            ApplyPendingSelection();
        }
    }

    private void LoadViewsFromSettings()
    {
        var selectedId = _selectedView?.Id;

        Views.Clear();
        foreach (var view in _settings.Current.NeighbourViews)
        {
            Views.Add(view);
        }

        OnPropertyChanged(nameof(HasViews));
        RebuildNeighbourhoods();

        // The open view stays open (with its new rules, if edited); if it
        // was deleted, back to the table of views.
        var select = selectedId is null ? null : Views.FirstOrDefault(v => v.Id == selectedId);
        if (!ReferenceEquals(select, _selectedView))
        {
            SelectedView = select;
        }
        else
        {
            // Same view, maybe with new rules.
            OnPropertyChanged(nameof(ViewRulesText));
            RebuildItems();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // Only when the views themselves changed - selecting one saves too.
        var current = settings.NeighbourViews;
        if (current.Count != Views.Count || current.Where((v, i) => !ReferenceEquals(v, Views[i])).Any())
        {
            LoadViewsFromSettings();
        }

        _autoRefreshTimer.Interval = AutoRefreshInterval;
    }

    /// <summary>
    /// A background refresh skips the loading overlay, so the table stays
    /// on screen - with its selection and scroll position - while it updates.
    /// </summary>
    private async Task LoadAsync(bool refresh, bool background = false)
    {
        IsBusy = true;
        ErrorMessage = null;
        if (!background)
        {
            LoadState.BeginLoad();
        }

        try
        {
            _snapshot = await _directory.GetAsync(refresh).ConfigureAwait(true);
            RebuildItems();
            RebuildNeighbourhoods();
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load switch neighbours");
            ErrorMessage = ex.ToUserMessage();
            LoadState.CompleteLoad(Items.Count, ItemsView.Cast<object>().Count());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RebuildItems()
    {
        var selectedKey = _selected?.Key;

        using (Items.BeginBatch())
        {
            Items.Clear();
            if (_snapshot is { } snapshot)
            {
                // Every neighbour with "All" picked (#221); a neighbourhood's
                // rules narrow it down.
                var neighbours = _selectedView is { } view
                    ? snapshot.For(view, id => _devices.Get(id)?.BestName)
                    : snapshot.Neighbours;

                var rows = neighbours
                    .Select(n => new NeighbourItemViewModel(
                        n,
                        snapshot.PortOf(n),
                        _devices.Get(n.SwitchDeviceId),
                        n.RemoteDeviceId is { } id ? _devices.Get(id) : null,
                        snapshot.IpOf(n)));

                // One neighbour seen on several ports shows only its live
                // link(s) - not the one on a switch that's gone offline. Down
                // ones first, as on mobile - what needs a look comes to the top.
                foreach (var row in Neighbours.PreferLiveLinks(rows, r => r.Neighbour, r => r.IsLinkUp, r => !r.IsSwitchDown)
                             .OrderBy(r => r.State == NeighbourState.Down ? 0 : r.State == NeighbourState.Up ? 1 : 2)
                             .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Items.Add(row);
                }
            }
        }

        if (_snapshot is not null)
        {
            LoadState.CompleteLoad(Items.Count, ItemsView.Cast<object>().Count());
        }

        OnPropertyChanged(nameof(UpCount));
        OnPropertyChanged(nameof(DownCount));
        OnPropertyChanged(nameof(OtherCount));
        OnPropertyChanged(nameof(HasOther));

        if (_pendingSelection is null && selectedKey is not null)
        {
            SelectedItem = Items.FirstOrDefault(i => i.Key == selectedKey);
        }

        ApplyPendingSelection();
    }

    private void ApplyPendingSelection()
    {
        if (_pendingSelection is not { } wanted || Items.Count == 0)
        {
            return;
        }

        _pendingSelection = null;
        var named = Items.Where(i => string.Equals(i.Name, wanted.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if ((named.FirstOrDefault(i => wanted.Mac is not null && string.Equals(i.Neighbour.Mac, wanted.Mac, StringComparison.OrdinalIgnoreCase)) ?? named.FirstOrDefault()) is { } match)
        {
            SearchText = string.Empty;
            ShowUp = ShowDown = ShowOther = true;
            SelectedItem = match;
        }
    }

    private void NewView()
    {
        if (_windows.ShowNeighbourViewEditor(null) is { } created)
        {
            _settings.Current.NeighbourViews.Add(created);
            _settings.Save();
            SelectedView = Views.FirstOrDefault(v => v.Id == created.Id);
        }
    }

    private NeighbourViewDefinition? ViewFrom(object? parameter) => parameter switch
    {
        NeighbourhoodChipViewModel chip => chip.Definition,
        NeighbourViewDefinition definition => definition,
        _ => _selectedView,
    };

    private void EditView(NeighbourViewDefinition? view)
    {
        if (view is null || _windows.ShowNeighbourViewEditor(view) is not { } edited)
        {
            return;
        }

        var views = _settings.Current.NeighbourViews;
        var index = views.FindIndex(v => v.Id == view.Id);
        if (index >= 0)
        {
            views[index] = edited;
            _settings.Save();
        }
    }

    private void DeleteView(NeighbourViewDefinition? view)
    {
        if (view is null
            || !_windows.Confirm("Delete neighbourhood", $"Delete the neighbourhood \"{view.Name}\"? {Confirmations.CannotBeUndone} Only the filter goes - nothing changes in LibreNMS.", "Delete", destructive: true))
        {
            return;
        }

        _settings.Current.NeighbourViews.RemoveAll(v => v.Id == view.Id);
        _settings.Save();
    }

    /// <summary>The chip's "Show on network map" - draw this neighbourhood's neighbours on the map.</summary>
    private void ToggleShowOnMap(NeighbourViewDefinition? view)
    {
        if (view is null || _settings.Current.NeighbourViews.FirstOrDefault(v => v.Id == view.Id) is not { } stored)
        {
            return;
        }

        // A copy, so the settings' list changes and every listener (this
        // tab, the network map) notices.
        var updated = stored.Clone();
        updated.ShowOnMap = !updated.ShowOnMap;
        var views = _settings.Current.NeighbourViews;
        views[views.IndexOf(stored)] = updated;
        _settings.Save();
    }

    /// <summary>The chip's "Move left" / "Move right" - neighbourhoods keep the order they're given.</summary>
    private void Move(NeighbourViewDefinition? view, int by)
    {
        var views = _settings.Current.NeighbourViews;
        var index = view is null ? -1 : views.FindIndex(v => v.Id == view.Id);
        var target = index + by;
        if (index < 0 || target < 0 || target >= views.Count)
        {
            return;
        }

        (views[index], views[target]) = (views[target], views[index]);
        _settings.Save();
    }

    /// <summary>The chip row: All, then each neighbourhood with how many neighbours it holds, once they're in.</summary>
    private void RebuildNeighbourhoods()
    {
        var snapshot = _snapshot;
        int? CountOf(IEnumerable<Neighbour> neighbours) => snapshot is null
            ? null
            : neighbours.Select(Neighbours.IdentityKey).Distinct(StringComparer.Ordinal).Count();

        Neighbourhoods.Clear();
        Neighbourhoods.Add(new NeighbourhoodChipViewModel(null, "All", CountOf(snapshot?.Neighbours ?? Array.Empty<Neighbour>()), _selectedView is null, isFirst: true, isLast: true));
        for (var i = 0; i < Views.Count; i++)
        {
            var view = Views[i];
            Neighbourhoods.Add(new NeighbourhoodChipViewModel(
                view,
                view.Name,
                snapshot is null ? null : CountOf(snapshot.For(view, id => _devices.Get(id)?.BestName)),
                _selectedView?.Id == view.Id,
                isFirst: i == 0,
                isLast: i == Views.Count - 1));
        }
    }

    /// <summary>Shift-click on a pill: show only that one.</summary>
    public void IsolateState(NeighbourState state)
    {
        ShowUp = state == NeighbourState.Up;
        ShowDown = state == NeighbourState.Down;
        ShowOther = state == NeighbourState.Other;
    }

    private bool IsStateShown(NeighbourState state) => state switch
    {
        NeighbourState.Up => _showUp,
        NeighbourState.Down => _showDown,
        _ => _showOther,
    };

    private void SetFilter(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        ItemsView.Refresh();
        OnPropertyChanged(nameof(HasAnyFilterApplied));
        LoadState.UpdateVisibleCount(ItemsView.Cast<object>().Count());
    }

    public void Dispose()
    {
        _autoRefreshTimer.Stop();
        _autoRefreshTimer.Tick -= OnAutoRefreshTick;
        _settings.Changed -= OnSettingsChanged;
    }
}

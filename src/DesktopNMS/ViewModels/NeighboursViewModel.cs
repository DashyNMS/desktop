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
/// see over LLDP/CDP - access points, antennas, cameras, anything - each
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
    private int _graphVersion;

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
        Items = new ObservableCollection<NeighbourItemViewModel>();
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is NeighbourItemViewModel n && IsStateShown(n.State) && n.Matches(SearchText);

        // The port graphs LibreNMS draws for any port (checked live - the
        // rest, like PAgP or FDB count, only exist on some).
        PortGraphs = new ObservableCollection<PortGraphViewModel>
        {
            new("port_bits", "Traffic"),
            new("port_upkts", "Unicast packets"),
            new("port_nupkts", "Broadcast and multicast packets"),
            new("port_errors", "Errors"),
        };

        TimeRange = new GraphTimeRangeViewModel();
        TimeRange.Changed += (_, _) => _ = LoadGraphsAsync();

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(refresh: true), () => _session.IsConnected && !IsBusy);
        ClearFiltersCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            ShowUp = ShowDown = ShowOther = true;
        });
        NewViewCommand = new RelayCommand(NewView);
        EditViewCommand = new RelayCommand(EditView, () => _selectedView is not null);
        DeleteViewCommand = new RelayCommand(DeleteView, () => _selectedView is not null);
        SelectViewCommand = new RelayCommand(parameter =>
        {
            if (parameter is NeighbourViewDefinition view)
            {
                SelectedView = Views.FirstOrDefault(v => v.Id == view.Id);
            }
        });
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

        LoadViewsFromSettings();
        _settings.Changed += OnSettingsChanged;
    }

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
                EditViewCommand.RaiseCanExecuteChanged();
                DeleteViewCommand.RaiseCanExecuteChanged();

                if (value is not null && _settings.Current.LastNeighbourViewId != value.Id)
                {
                    _settings.Current.LastNeighbourViewId = value.Id;
                    _settings.Save();
                }

                SelectedItem = null;
                RebuildItems();

                if (value is not null && _snapshot is null && _hasLoadedOnce)
                {
                    _ = LoadAsync(refresh: false);
                }
            }
        }
    }

    public bool HasSelectedView => _selectedView is not null;

    /// <summary>"System description contains Riedel Bolero" - what the selected view looks for.</summary>
    public string ViewRulesText => _selectedView is { } view ? DescribeRules(view) : string.Empty;

    public ObservableCollection<NeighbourItemViewModel> Items { get; }

    public ICollectionView ItemsView { get; }

    public GraphTimeRangeViewModel TimeRange { get; }

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
                _ = LoadGraphsAsync();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>The selected neighbour's switch port graphs - traffic, packets and errors - side by side.</summary>
    public ObservableCollection<PortGraphViewModel> PortGraphs { get; }

    /// <summary>Loads once, lazily, the first time the tab is shown - same convention as every other tab.</summary>
    public void OnShown()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _hasLoadedOnce = true;
        _ = LoadAsync(refresh: false);
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

    /// <summary>"System description contains X and switch starts with Y".</summary>
    public static string DescribeRules(NeighbourViewDefinition view)
    {
        var rules = view.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (rules.Count == 0)
        {
            return "No rules yet - edit the view to add some.";
        }

        var text = string.Join(view.MatchAll ? " and " : " or ", rules.Select(r =>
            $"{NeighbourViewText.FieldName(r.Field).ToLowerInvariant()} {NeighbourViewText.OperatorName(r.Operator)} \"{r.Value.Trim()}\""));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private void LoadViewsFromSettings()
    {
        var selectedId = _selectedView?.Id ?? _settings.Current.LastNeighbourViewId;

        Views.Clear();
        foreach (var view in _settings.Current.NeighbourViews)
        {
            Views.Add(view);
        }

        OnPropertyChanged(nameof(HasViews));

        var select = Views.FirstOrDefault(v => v.Id == selectedId) ?? Views.FirstOrDefault();
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
    }

    private async Task LoadAsync(bool refresh)
    {
        IsBusy = true;
        ErrorMessage = null;
        LoadState.BeginLoad();

        try
        {
            _snapshot = await _directory.GetAsync(refresh).ConfigureAwait(true);
            RebuildItems();
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

        Items.Clear();
        if (_snapshot is { } snapshot && _selectedView is { } view)
        {
            foreach (var n in snapshot.For(view, id => _devices.Get(id)?.BestName))
            {
                Items.Add(new NeighbourItemViewModel(
                    n,
                    snapshot.PortOf(n),
                    _devices.Get(n.SwitchDeviceId),
                    n.RemoteDeviceId is { } id ? _devices.Get(id) : null));
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

    private void EditView()
    {
        if (_selectedView is not { } view || _windows.ShowNeighbourViewEditor(view) is not { } edited)
        {
            return;
        }

        var views = _settings.Current.NeighbourViews;
        var index = views.FindIndex(v => v.Id == view.Id);
        if (index >= 0)
        {
            views[index] = edited;
            _settings.Save();
            SelectedView = Views.FirstOrDefault(v => v.Id == edited.Id);
        }
    }

    private void DeleteView()
    {
        if (_selectedView is not { } view
            || !_windows.Confirm("Delete view", $"Delete the \"{view.Name}\" view? This only removes the view - nothing changes in LibreNMS."))
        {
            return;
        }

        _settings.Current.NeighbourViews.RemoveAll(v => v.Id == view.Id);
        _settings.Save();
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

    /// <summary>Every port graph for the selected neighbour, all at once; a newer selection or time range wins over one still loading.</summary>
    private async Task LoadGraphsAsync()
    {
        var version = ++_graphVersion;

        if (_selected is not { PortIfName: { } ifName } item)
        {
            foreach (var graph in PortGraphs)
            {
                graph.Show(null, _selected is null ? null : "LibreNMS doesn't know this neighbour's switch port.");
            }

            return;
        }

        var range = TimeRange.ToTimeRange();

        async Task LoadOne(PortGraphViewModel graph)
        {
            graph.BeginLoad();
            try
            {
                var svg = await _client.Graphs.GetPortSvgAsync(item.SwitchDeviceId, ifName, graph.GraphType, range, width: 560, height: 150).ConfigureAwait(true);
                if (version == _graphVersion)
                {
                    graph.Show(GraphSvgTheming.ApplyCurrentTheme(svg), null);
                }
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogWarning(ex, "Could not load the {GraphType} graph for neighbour {Name}", graph.GraphType, item.Name);
                if (version == _graphVersion)
                {
                    graph.Show(null, ex.ToUserMessage());
                }
            }
        }

        await Task.WhenAll(PortGraphs.Select(LoadOne)).ConfigureAwait(true);
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}

/// <summary>How a neighbour reads at a glance - drives its status dot and the Up/Down pills.</summary>
public enum NeighbourState
{
    Up,
    Down,

    /// <summary>Shut-down port, disabled or in-maintenance device or switch, or a neighbour LibreNMS no longer sees.</summary>
    Other,
}

/// <summary>Display names for view rules - shared by the tab and the view editor.</summary>
public static class NeighbourViewText
{
    public static string FieldName(NeighbourRuleField field) => field switch
    {
        NeighbourRuleField.SystemName => "System name",
        NeighbourRuleField.SystemDescription => "System description",
        NeighbourRuleField.PortId => "Port ID",
        NeighbourRuleField.Protocol => "Protocol",
        NeighbourRuleField.Switch => "Switch",
        NeighbourRuleField.SwitchPortDescription => "Switch port description",
        _ => field.ToString(),
    };

    public static string OperatorName(NeighbourRuleOperator op) => op switch
    {
        NeighbourRuleOperator.Contains => "contains",
        NeighbourRuleOperator.StartsWith => "starts with",
        NeighbourRuleOperator.Equals => "equals",
        NeighbourRuleOperator.DoesNotContain => "does not contain",
        NeighbourRuleOperator.Matches => "matches regex",
        _ => op.ToString(),
    };
}

/// <summary>One neighbour row.</summary>
public sealed class NeighbourItemViewModel
{
    public NeighbourItemViewModel(Neighbour neighbour, Port? port, Device? @switch, Device? device)
    {
        Neighbour = neighbour;
        Port = port;
        Device = device;
        SwitchName = @switch?.BestName ?? $"device {neighbour.SwitchDeviceId}";

        // A switch that's down (or disabled) isn't being polled, so its
        // ports keep their last reading - often weeks-old "up".
        IsSwitchDown = @switch is { Status: false, Disabled: false };
        var switchDisabled = @switch is { Disabled: true };

        if (device is not null)
        {
            // LibreNMS monitors it itself - its own state says it best.
            State = device.State switch
            {
                DeviceState.Up => NeighbourState.Up,
                DeviceState.Down => NeighbourState.Down,
                _ => NeighbourState.Other,
            };
            StateText = device.State switch
            {
                DeviceState.Up => "Up",
                DeviceState.Down => "Down",
                DeviceState.Maintenance => "Maintenance",
                DeviceState.Disabled => "Disabled",
                DeviceState.Ignored => "Ignored",
                _ => "Unknown",
            };
            return;
        }

        (State, StateText) =
            IsSwitchDown ? (NeighbourState.Down, "Down")
            : switchDisabled ? (NeighbourState.Other, "Switch disabled")
            : !neighbour.Active ? (NeighbourState.Other, "Not seen")
            : port is null ? (NeighbourState.Other, "Unknown port")
            : string.Equals(port.IfAdminStatus, "down", StringComparison.OrdinalIgnoreCase) ? (NeighbourState.Other, "Port shut down")
            : port.IsUp ? (NeighbourState.Up, "Up")
            : (NeighbourState.Down, "Down");
    }

    public Neighbour Neighbour { get; }

    public Port? Port { get; }

    /// <summary>The LibreNMS device this neighbour is, when LibreNMS monitors it.</summary>
    public Device? Device { get; }

    public bool IsMonitored => Neighbour.IsMonitored;

    /// <summary>Tells two rows apart across a reload - a neighbour can be on more than one port.</summary>
    public string Key => Neighbour.Name + "|" + (Neighbour.Mac ?? Neighbour.RemotePort) + "@" + Neighbour.SwitchPortId.ToString(CultureInfo.InvariantCulture);

    public string Name => Neighbour.Name;

    public string DescriptionText => Neighbour.Description ?? string.Empty;

    public string MacText => Neighbour.Mac ?? string.Empty;

    public int SwitchDeviceId => Neighbour.SwitchDeviceId;

    public string SwitchName { get; }

    public bool IsSwitchDown { get; }

    public string PortText => Port?.DisplayName ?? "-";

    /// <summary>The port's ifName - what LibreNMS's port graph route takes.</summary>
    public string? PortIfName => string.IsNullOrWhiteSpace(Port?.IfName) ? null : Port.IfName;

    public NeighbourState State { get; }

    public string StateText { get; }

    /// <summary>The same colours as everywhere else: green up, red down, grey otherwise.</summary>
    public AlertSeverity Severity => State switch
    {
        NeighbourState.Up => AlertSeverity.Ok,
        NeighbourState.Down => AlertSeverity.Critical,
        _ => AlertSeverity.Unknown,
    };

    public string SpeedText => !IsSwitchDown && Port?.IfSpeed is { } speed && speed > 0 ? LinkUtilisation.Rate(speed) : "-";

    public double InBps => (Port?.IfInOctetsRate ?? 0) * 8;

    public double OutBps => (Port?.IfOutOctetsRate ?? 0) * 8;

    /// <summary>Traffic towards the neighbour (the switch port's out) and from it (the port's in) - only while its switch is polled and the port is up.</summary>
    public string TrafficText => Port is null || IsSwitchDown || !Port.IsUp
        ? "-"
        : $"{Rate(OutBps)} to / {Rate(InBps)} from";

    public string ToolTip
    {
        get
        {
            var text = Neighbour.IsUnnamed
                ? $"This doesn't announce a name - its port/MAC is {Neighbour.RemotePort ?? "unknown"}."
                : Name;
            text += $" Plugged into {SwitchName} {PortText}.";
            if (IsMonitored)
            {
                text += " LibreNMS monitors it - click to open it.";
            }
            else if (IsSwitchDown)
            {
                text += $" {SwitchName} is down, so this counts as down too.";
            }

            return text;
        }
    }

    public bool Matches(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        var t = term.Trim();
        return Name.Contains(t, StringComparison.OrdinalIgnoreCase)
            || DescriptionText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || SwitchName.Contains(t, StringComparison.OrdinalIgnoreCase)
            || PortText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || MacText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || StateText.Contains(t, StringComparison.OrdinalIgnoreCase);
    }

    private static string Rate(double bps) => bps <= 0 ? "0 bps" : LinkUtilisation.Rate(bps);
}

/// <summary>One of the selected neighbour's port graphs.</summary>
public sealed class PortGraphViewModel : ObservableObject
{
    private string? _svg;
    private bool _isLoading;
    private string? _errorMessage;

    public PortGraphViewModel(string graphType, string title)
    {
        GraphType = graphType;
        Title = title;
    }

    /// <summary>LibreNMS's graph name, e.g. "port_errors".</summary>
    public string GraphType { get; }

    public string Title { get; }

    public string? Svg
    {
        get => _svg;
        private set => SetProperty(ref _svg, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
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

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public void BeginLoad()
    {
        Svg = null;
        ErrorMessage = null;
        IsLoading = true;
    }

    public void Show(string? svg, string? error)
    {
        Svg = svg;
        ErrorMessage = error;
        IsLoading = false;
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The Access points page under Devices (#55): every AP the switches see
/// over LLDP - name, model, and the switch port it's plugged into, with
/// that port's state and traffic - and the traffic graph of whichever one
/// is selected. LibreNMS's own per-AP data (clients, channel, radio use)
/// isn't in its API, so the switch port is what there is to show; see
/// <see cref="AccessPoints"/>.
/// </summary>
public sealed class AccessPointsViewModel : ObservableObject
{
    private readonly IAccessPointDirectory _directory;
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly IDeviceCache _devices;
    private readonly IWindowService _windows;
    private readonly ILogger<AccessPointsViewModel> _logger;

    private bool _hasLoadedOnce;
    private bool _isBusy;
    private string? _errorMessage;
    private string _searchText = string.Empty;
    private bool _showUp = true;
    private bool _showDown = true;
    private bool _showOther = true;
    private AccessPointItemViewModel? _selected;
    private string? _pendingSelection;
    private string? _pendingSelectionMac;
    private int _graphVersion;

    public AccessPointsViewModel(
        IAccessPointDirectory directory,
        ILibreNmsClient client,
        ISessionService session,
        IDeviceCache devices,
        IWindowService windows,
        ILogger<AccessPointsViewModel> logger)
    {
        _directory = directory;
        _client = client;
        _session = session;
        _devices = devices;
        _windows = windows;
        _logger = logger;

        Items = new ObservableCollection<AccessPointItemViewModel>();
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is AccessPointItemViewModel ap && IsStateShown(ap.State) && ap.Matches(SearchText);

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
        OpenSwitchCommand = new RelayCommand(parameter =>
        {
            if (parameter is AccessPointItemViewModel item)
            {
                _windows.ShowDeviceDetail(item.SwitchDeviceId);
            }
        });
    }

    public ObservableCollection<AccessPointItemViewModel> Items { get; }

    public ICollectionView ItemsView { get; }

    public GraphTimeRangeViewModel TimeRange { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    public RelayCommand OpenSwitchCommand { get; }

    /// <summary>Drives the loading/empty/no-matches split on the grid.</summary>
    public ListLoadState LoadState { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ItemsView.Refresh();
                OnPropertyChanged(nameof(HasAnyFilterApplied));
                LoadState.UpdateVisibleCount(ItemsView.Cast<object>().Count());
            }
        }
    }

    public bool HasAnyFilterApplied => !string.IsNullOrEmpty(SearchText) || !ShowUp || !ShowDown || !ShowOther;

    /// <summary>The "port up" pill - APs whose switch port is up. That says the cable's live, not that the AP has joined its controller: LibreNMS's API doesn't say that.</summary>
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

    /// <summary>Shut-down ports, and APs LibreNMS no longer sees or whose port it doesn't know.</summary>
    public bool ShowOther
    {
        get => _showOther;
        set => SetFilter(ref _showOther, value);
    }

    public int UpCount => Items.Count(i => i.State == AccessPointState.Up);

    public int DownCount => Items.Count(i => i.State == AccessPointState.Down);

    public int OtherCount => Items.Count(i => i.State is AccessPointState.AdminDown or AccessPointState.Unknown);

    public bool HasOther => OtherCount > 0;

    /// <summary>Shift-click on a pill: show only that one.</summary>
    public void IsolateState(AccessPointState state)
    {
        ShowUp = state == AccessPointState.Up;
        ShowDown = state == AccessPointState.Down;
        ShowOther = state is AccessPointState.AdminDown or AccessPointState.Unknown;
    }

    private bool IsStateShown(AccessPointState state) => state switch
    {
        AccessPointState.Up => _showUp,
        AccessPointState.Down => _showDown,
        _ => _showOther,
    };

    private void SetFilter(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            ItemsView.Refresh();
            OnPropertyChanged(nameof(HasAnyFilterApplied));
            LoadState.UpdateVisibleCount(ItemsView.Cast<object>().Count());
        }
    }

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

    public AccessPointItemViewModel? SelectedItem
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

    /// <summary>The selected AP's switch port graphs - traffic, packets and errors - shown side by side.</summary>
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

    /// <summary>Selects the AP with this name - now if it's listed, or once the list loads. The MAC picks out which one, for several "Unknown AP"s.</summary>
    public void Select(string name, string? mac = null)
    {
        _pendingSelection = name;
        _pendingSelectionMac = mac;
        ApplyPendingSelection();
    }

    private async Task LoadAsync(bool refresh)
    {
        IsBusy = true;
        ErrorMessage = null;
        LoadState.BeginLoad();

        var selectedKey = _selected?.Key;

        try
        {
            var snapshot = await _directory.GetAsync(refresh).ConfigureAwait(true);

            Items.Clear();
            foreach (var ap in snapshot.AccessPoints)
            {
                Items.Add(new AccessPointItemViewModel(ap, snapshot.PortOf(ap), _devices.Get(ap.SwitchDeviceId)));
            }

            if (_pendingSelection is null && selectedKey is not null)
            {
                SelectedItem = Items.FirstOrDefault(i => i.Key == selectedKey);
            }

            ApplyPendingSelection();
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load access points");
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
            LoadState.CompleteLoad(Items.Count, ItemsView.Cast<object>().Count());
            OnPropertyChanged(nameof(UpCount));
            OnPropertyChanged(nameof(DownCount));
            OnPropertyChanged(nameof(OtherCount));
            OnPropertyChanged(nameof(HasOther));
        }
    }

    private void ApplyPendingSelection()
    {
        if (_pendingSelection is not { } name || Items.Count == 0)
        {
            return;
        }

        var mac = _pendingSelectionMac;
        _pendingSelection = null;
        _pendingSelectionMac = null;

        var named = Items.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if ((named.FirstOrDefault(i => mac is not null && string.Equals(i.AccessPoint.Mac, mac, StringComparison.OrdinalIgnoreCase)) ?? named.FirstOrDefault()) is { } match)
        {
            SearchText = string.Empty;
            SelectedItem = match;
        }
    }

    /// <summary>Every port graph for the selected AP, all at once; a newer selection or time range wins over one still loading.</summary>
    private async Task LoadGraphsAsync()
    {
        var version = ++_graphVersion;

        if (_selected is not { PortIfName: { } ifName } item)
        {
            foreach (var graph in PortGraphs)
            {
                graph.Show(null, _selected is null ? null : "LibreNMS doesn't know this AP's switch port.");
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
                _logger.LogWarning(ex, "Could not load the {GraphType} graph for access point {Name}", graph.GraphType, item.Name);
                if (version == _graphVersion)
                {
                    graph.Show(null, ex.ToUserMessage());
                }
            }
        }

        await Task.WhenAll(PortGraphs.Select(LoadOne)).ConfigureAwait(true);
    }
}

/// <summary>One of the selected access point's port graphs.</summary>
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

/// <summary>How an access point's switch port reads at a glance - drives its status dot.</summary>
public enum AccessPointState
{
    Up,
    Down,

    /// <summary>The port is shut down - deliberate, not a fault.</summary>
    AdminDown,

    /// <summary>LibreNMS no longer sees the AP on this port, or doesn't know the port.</summary>
    Unknown,
}

/// <summary>One access point row.</summary>
public sealed class AccessPointItemViewModel
{
    public AccessPointItemViewModel(AccessPoint accessPoint, Port? port, Device? @switch)
    {
        AccessPoint = accessPoint;
        Port = port;
        SwitchName = @switch?.BestName ?? $"device {accessPoint.SwitchDeviceId}";

        State = !accessPoint.Active || port is null ? AccessPointState.Unknown
            : string.Equals(port.IfAdminStatus, "down", StringComparison.OrdinalIgnoreCase) ? AccessPointState.AdminDown
            : port.IsUp ? AccessPointState.Up
            : AccessPointState.Down;
    }

    public AccessPoint AccessPoint { get; }

    public Port? Port { get; }

    /// <summary>Tells two rows apart across a reload - an AP can be on more than one port.</summary>
    public string Key => AccessPoint.Name + "@" + AccessPoint.SwitchPortId.ToString(CultureInfo.InvariantCulture);

    public string Name => AccessPoint.Name;

    public string ModelText => AccessPoint.Model ?? "-";

    public string MacText => AccessPoint.Mac ?? string.Empty;

    public int SwitchDeviceId => AccessPoint.SwitchDeviceId;

    public string SwitchName { get; }

    public string PortText => Port?.DisplayName ?? "-";

    /// <summary>The port's ifName - what LibreNMS's port graph route takes.</summary>
    public string? PortIfName => string.IsNullOrWhiteSpace(Port?.IfName) ? null : Port.IfName;

    public AccessPointState State { get; }

    /// <summary>The same colours as everywhere else: green up, red down, grey otherwise.</summary>
    public AlertSeverity Severity => State switch
    {
        AccessPointState.Up => AlertSeverity.Ok,
        AccessPointState.Down => AlertSeverity.Critical,
        _ => AlertSeverity.Unknown,
    };

    /// <summary>
    /// The switch port's state - "Port up", not "Up": a live port with the
    /// AP still listed over LLDP doesn't mean the AP has joined its
    /// controller, and only the controller knows that.
    /// </summary>
    public string StateText => State switch
    {
        AccessPointState.Up => "Port up",
        AccessPointState.Down => "Port down",
        AccessPointState.AdminDown => "Port shut down",
        _ => AccessPoint.Active ? "Unknown port" : "Not seen",
    };

    public string SpeedText => Port?.IfSpeed is { } speed && speed > 0 ? LinkUtilisation.Rate(speed) : "-";

    public double InBps => (Port?.IfInOctetsRate ?? 0) * 8;

    public double OutBps => (Port?.IfOutOctetsRate ?? 0) * 8;

    /// <summary>Traffic towards the AP (the switch port's out) and from it (the port's in).</summary>
    public string TrafficText => Port is null || State != AccessPointState.Up
        ? "-"
        : $"{Rate(OutBps)} to / {Rate(InBps)} from";

    public string ToolTip => AccessPoint.IsUnnamed
        ? $"This AP doesn't announce a name over LLDP - its MAC is {(MacText.Length > 0 ? MacText : "unknown")}. Plugged into {SwitchName} {PortText}."
        : $"{Name} ({ModelText}) on {SwitchName} {PortText}" + (MacText.Length > 0 ? $" - MAC {MacText}" : string.Empty);

    public bool Matches(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        var t = term.Trim();
        return Name.Contains(t, StringComparison.OrdinalIgnoreCase)
            || ModelText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || SwitchName.Contains(t, StringComparison.OrdinalIgnoreCase)
            || PortText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || MacText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || StateText.Contains(t, StringComparison.OrdinalIgnoreCase);
    }

    private static string Rate(double bps) => bps <= 0 ? "0 bps" : LinkUtilisation.Rate(bps);
}

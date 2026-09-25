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
    private AccessPointItemViewModel? _selected;
    private string? _pendingSelection;
    private string? _graphSvg;
    private bool _isGraphLoading;
    private string? _graphError;
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
        ItemsView.Filter = item => item is AccessPointItemViewModel ap && ap.Matches(SearchText);

        TimeRange = new GraphTimeRangeViewModel();
        TimeRange.Changed += (_, _) => _ = LoadGraphAsync();

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(refresh: true), () => _session.IsConnected && !IsBusy);
        ClearFiltersCommand = new RelayCommand(() => SearchText = string.Empty);
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

    public bool HasAnyFilterApplied => !string.IsNullOrEmpty(SearchText);

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

    /// <summary>"50 access points - 48 up - AP-345 x26, AP-535 x23".</summary>
    public string SummaryText => AccessPointItemViewModel.Summarise(Items);

    public AccessPointItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                GraphSvg = null;
                GraphError = null;
                _ = LoadGraphAsync();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>The selected AP's switch port traffic graph, themed.</summary>
    public string? GraphSvg
    {
        get => _graphSvg;
        private set => SetProperty(ref _graphSvg, value);
    }

    public bool IsGraphLoading
    {
        get => _isGraphLoading;
        private set => SetProperty(ref _isGraphLoading, value);
    }

    public string? GraphError
    {
        get => _graphError;
        private set
        {
            if (SetProperty(ref _graphError, value))
            {
                OnPropertyChanged(nameof(HasGraphError));
            }
        }
    }

    public bool HasGraphError => !string.IsNullOrEmpty(_graphError);

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

    /// <summary>Selects the AP with this name - now if it's listed, or once the list loads.</summary>
    public void Select(string name)
    {
        _pendingSelection = name;
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

            OnPropertyChanged(nameof(SummaryText));

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
        }
    }

    private void ApplyPendingSelection()
    {
        if (_pendingSelection is not { } name || Items.Count == 0)
        {
            return;
        }

        _pendingSelection = null;
        if (Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match)
        {
            SearchText = string.Empty;
            SelectedItem = match;
        }
    }

    private async Task LoadGraphAsync()
    {
        var version = ++_graphVersion;

        if (_selected is not { PortIfName: { } ifName } item)
        {
            IsGraphLoading = false;
            GraphError = _selected is null ? null : "LibreNMS doesn't know this AP's switch port.";
            return;
        }

        IsGraphLoading = true;
        try
        {
            var svg = await _client.Graphs.GetPortSvgAsync(item.SwitchDeviceId, ifName, "port_bits", TimeRange.ToTimeRange(), width: 900, height: 220).ConfigureAwait(true);
            if (version == _graphVersion)
            {
                GraphSvg = GraphSvgTheming.ApplyCurrentTheme(svg);
                GraphError = null;
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load the traffic graph for access point {Name}", item.Name);
            if (version == _graphVersion)
            {
                GraphSvg = null;
                GraphError = ex.ToUserMessage();
            }
        }
        finally
        {
            if (version == _graphVersion)
            {
                IsGraphLoading = false;
            }
        }
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

    public string StateText => State switch
    {
        AccessPointState.Up => "Up",
        AccessPointState.Down => "Down",
        AccessPointState.AdminDown => "Shut down",
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
        ? $"This AP doesn't announce a name - it's listed by its MAC address. Plugged into {SwitchName} {PortText}."
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

    /// <summary>"50 access points - 48 up - AP-345 x26, AP-535 x23, AP-567 x1".</summary>
    public static string Summarise(IReadOnlyCollection<AccessPointItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        // An AP seen on two ports counts once - by name, for the named ones.
        var distinct = items.GroupBy(i => i.AccessPoint.IsUnnamed ? i.Key : i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(i => i.State).First())
            .ToList();

        var up = distinct.Count(i => i.State == AccessPointState.Up);
        var models = distinct
            .Where(i => i.AccessPoint.Model is not null)
            .GroupBy(i => i.AccessPoint.Model!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ×{g.Count()}");

        var parts = new List<string>
        {
            distinct.Count.ToString(CultureInfo.CurrentCulture) + (distinct.Count == 1 ? " access point" : " access points"),
            up.ToString(CultureInfo.CurrentCulture) + " up",
        };

        var modelText = string.Join(", ", models);
        if (modelText.Length > 0)
        {
            parts.Add(modelText);
        }

        return string.Join(" - ", parts);
    }

    private static string Rate(double bps) => bps <= 0 ? "0 bps" : LinkUtilisation.Rate(bps);
}

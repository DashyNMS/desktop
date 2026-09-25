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
    private string _viewSearchText = string.Empty;

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

        PortGraphs = new PortGraphsPanelViewModel(client, settings, logger);

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
            if (ViewFrom(parameter) is { } view)
            {
                SelectedView = Views.FirstOrDefault(v => v.Id == view.Id);
            }
        });
        ShowViewListCommand = new RelayCommand(ShowViewList);
        ClearViewSearchCommand = new RelayCommand(() => ViewSearchText = string.Empty);

        ViewRows = new ObservableCollection<NeighbourViewRowViewModel>();
        ViewRowsView = CollectionViewSource.GetDefaultView(ViewRows);
        ViewRowsView.Filter = row => row is NeighbourViewRowViewModel r && r.Matches(ViewSearchText);
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
                OnPropertyChanged(nameof(IsViewListMode));
                OnPropertyChanged(nameof(ViewRulesText));

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

    /// <summary>No view open - the tab shows the table of every view, which is where clicking the tab itself lands.</summary>
    public bool IsViewListMode => _selectedView is null;

    /// <summary>The table of views: each one's name, rules, how much it matches now, and whether it's on the map.</summary>
    public ObservableCollection<NeighbourViewRowViewModel> ViewRows { get; }

    public ICollectionView ViewRowsView { get; }

    public string ViewSearchText
    {
        get => _viewSearchText;
        set
        {
            if (SetProperty(ref _viewSearchText, value))
            {
                ViewRowsView.Refresh();
                OnPropertyChanged(nameof(HasViewSearch));
                OnPropertyChanged(nameof(ShowNoViewMatches));
            }
        }
    }

    public bool HasViewSearch => !string.IsNullOrEmpty(_viewSearchText);

    public bool ShowNoViewMatches => HasViews && !ViewRowsView.Cast<object>().Any();

    public RelayCommand ShowViewListCommand { get; }

    public RelayCommand ClearViewSearchCommand { get; }

    /// <summary>Back to the table of every view.</summary>
    public void ShowViewList() => SelectedView = null;

    /// <summary>"System description contains X and switch starts with Y" - what the selected view looks for.</summary>
    public string ViewRulesText => _selectedView is { } view ? DescribeRules(view) : string.Empty;

    public ObservableCollection<NeighbourItemViewModel> Items { get; }

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
        var selectedId = _selectedView?.Id;

        Views.Clear();
        foreach (var view in _settings.Current.NeighbourViews)
        {
            Views.Add(view);
        }

        OnPropertyChanged(nameof(HasViews));
        RebuildViewRows();

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
            RebuildViewRows();
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
            var rows = snapshot.For(view, id => _devices.Get(id)?.BestName)
                .Select(n => new NeighbourItemViewModel(
                    n,
                    snapshot.PortOf(n),
                    _devices.Get(n.SwitchDeviceId),
                    n.RemoteDeviceId is { } id ? _devices.Get(id) : null,
                    snapshot.IpOf(n)));

            // One neighbour seen on several ports shows only its live
            // link(s) - not the one on a switch that's gone offline.
            foreach (var row in Neighbours.PreferLiveLinks(rows, r => r.Neighbour, r => r.IsLinkUp, r => !r.IsSwitchDown))
            {
                Items.Add(row);
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
        NeighbourViewRowViewModel row => row.Definition,
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
            || !_windows.Confirm("Delete view", $"Delete the \"{view.Name}\" view? This only removes the view - nothing changes in LibreNMS."))
        {
            return;
        }

        _settings.Current.NeighbourViews.RemoveAll(v => v.Id == view.Id);
        _settings.Save();
    }

    /// <summary>The views table - with how many neighbours each matches, once the neighbours are in.</summary>
    private void RebuildViewRows()
    {
        ViewRows.Clear();
        foreach (var view in Views)
        {
            int? count = _snapshot is { } snapshot
                ? snapshot.For(view, id => _devices.Get(id)?.BestName).Select(Neighbours.IdentityKey).Distinct(StringComparer.Ordinal).Count()
                : null;
            ViewRows.Add(new NeighbourViewRowViewModel(view, DescribeRules(view), count));
        }

        OnPropertyChanged(nameof(ShowNoViewMatches));
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
    /// <param name="arpIp">The IP the fleet's ARP tables have for its MAC, for one LibreNMS doesn't monitor.</param>
    public NeighbourItemViewModel(Neighbour neighbour, Port? port, Device? @switch, Device? device, string? arpIp = null)
    {
        Neighbour = neighbour;
        Port = port;
        Device = device;
        SwitchName = @switch?.BestName ?? $"device {neighbour.SwitchDeviceId}";
        IpText = device?.Ip ?? arpIp ?? string.Empty;

        // ifLastChange is the switch's sysUpTime (hundredths of a second) at
        // the change; the switch's uptime now, less that, is how long ago.
        if (port?.IfLastChange is { } changedAt && @switch is { Status: true, Uptime: > 0 } polled && polled.Uptime >= changedAt / 100)
        {
            SecondsSinceChange = polled.Uptime - (changedAt / 100);
        }

        // A switch that's down (or disabled) isn't being polled, so its
        // ports keep their last reading - often weeks-old "up".
        IsSwitchDown = @switch is { Status: false, Disabled: false };
        var switchDisabled = @switch is { Disabled: true };

        // A row is one neighbour on one switch port, so the link comes
        // first: a switch that's down, or a port that's down, means this
        // link is down - even when the neighbour is up somewhere else (seen
        // on a second switch, or an old link LibreNMS hasn't dropped).
        (State, StateText) =
            IsSwitchDown ? (NeighbourState.Down, "Down")
            : switchDisabled ? (NeighbourState.Other, "Switch disabled")
            : !neighbour.Active ? (NeighbourState.Other, "Not seen")
            : port is not null && string.Equals(port.IfAdminStatus, "down", StringComparison.OrdinalIgnoreCase) ? (NeighbourState.Other, "Port shut down")
            : port is not null && !port.IsUp ? (NeighbourState.Down, "Down")

            // The link's up (or its port unknown): a neighbour LibreNMS
            // monitors itself then has the last word on its own state.
            : device is not null ? device.State switch
            {
                DeviceState.Up => (NeighbourState.Up, "Up"),
                DeviceState.Down => (NeighbourState.Down, "Down"),
                DeviceState.Maintenance => (NeighbourState.Other, "Maintenance"),
                DeviceState.Disabled => (NeighbourState.Other, "Disabled"),
                DeviceState.Ignored => (NeighbourState.Other, "Ignored"),
                _ => (NeighbourState.Other, "Unknown"),
            }
            : port is null ? (NeighbourState.Other, "Unknown port")
            : (NeighbourState.Up, "Up");
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

    /// <summary>This link is up - its switch is polled and its port is up - whatever the neighbour itself is doing.</summary>
    public bool IsLinkUp => !IsSwitchDown && Neighbour.Active && Port is { } port && port.IsUp;

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

    /// <summary>The port's live figures mean something - its switch is polled and it's up. Otherwise they're stale or zero, and show as "-".</summary>
    private bool HasLiveFigures => Port is { } port && !IsSwitchDown && port.IsUp;

    /// <summary>The neighbour's IP: its LibreNMS device's, or from the fleet's ARP tables by its MAC.</summary>
    public string IpText { get; }

    /// <summary>Into the switch port - traffic from the neighbour. Sorts by <see cref="InBps"/>.</summary>
    public double InBps => HasLiveFigures ? (Port!.IfInOctetsRate ?? 0) * 8 : -1;

    public string InText => HasLiveFigures ? Rate(InBps) : "-";

    /// <summary>Out of the switch port - traffic to the neighbour.</summary>
    public double OutBps => HasLiveFigures ? (Port!.IfOutOctetsRate ?? 0) * 8 : -1;

    public string OutText => HasLiveFigures ? Rate(OutBps) : "-";

    /// <summary>In and out errors per second on the switch port, together.</summary>
    public double ErrorsPerSecond => HasLiveFigures ? (Port!.IfInErrorsRate ?? 0) + (Port.IfOutErrorsRate ?? 0) : -1;

    public string ErrorsText => HasLiveFigures ? PerSecond(ErrorsPerSecond) : "-";

    public string ErrorsToolTip => HasLiveFigures
        ? $"In {PerSecond(Port!.IfInErrorsRate ?? 0)}, out {PerSecond(Port.IfOutErrorsRate ?? 0)}"
        : "No live figures - the port or its switch is down.";

    public double PacketsInPerSecond => HasLiveFigures ? Port!.IfInUcastPktsRate ?? 0 : -1;

    public string PacketsInText => HasLiveFigures ? PerSecond(PacketsInPerSecond) : "-";

    public double PacketsOutPerSecond => HasLiveFigures ? Port!.IfOutUcastPktsRate ?? 0 : -1;

    public string PacketsOutText => HasLiveFigures ? PerSecond(PacketsOutPerSecond) : "-";

    /// <summary>The busier direction as a share of the port's speed.</summary>
    public double UtilisationPercent => HasLiveFigures && Port!.IfSpeed is { } speed && speed > 0
        ? Math.Min(100, Math.Max(InBps, OutBps) / speed * 100)
        : -1;

    public string UtilisationText => UtilisationPercent >= 0 ? UtilisationPercent.ToString(UtilisationPercent < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + "%" : "-";

    /// <summary>The switch port's own description (ifAlias).</summary>
    public string PortDescriptionText => Port?.IfAlias?.Trim() ?? string.Empty;

    public string DuplexText => Port?.IfDuplex?.Trim().ToLowerInvariant() switch
    {
        "fullduplex" or "full" => "Full",
        "halfduplex" or "half" => "Half",
        _ => "-",
    };

    public string VlanText => Port?.IfVlan is { } vlan && vlan > 0 ? vlan.ToString(CultureInfo.CurrentCulture) : "-";

    public string MtuText => Port?.IfMtu is { } mtu && mtu > 0 ? mtu.ToString(CultureInfo.CurrentCulture) : "-";

    /// <summary>How long since the port last went up or down, from the switch's uptime - null if it can't be told.</summary>
    public long? SecondsSinceChange { get; }

    public string LastChangeText => SecondsSinceChange is { } seconds ? RoutingSectionViewModel.FormatDuration(seconds) + " ago" : "-";

    public string ProtocolText => Neighbour.Protocol?.ToUpperInvariant() ?? string.Empty;

    /// <summary>The port the neighbour announces - often its MAC.</summary>
    public string AnnouncedPortText => Neighbour.RemotePort ?? string.Empty;

    public string ToolTip
    {
        get
        {
            var text = Neighbour.IsUnnamed
                ? $"This doesn't announce a name - its port/MAC is {Neighbour.RemotePort ?? "unknown"}."
                : Name;
            text += $" Plugged into {SwitchName} {PortText}.";
            if (IsSwitchDown)
            {
                text += $" {SwitchName} is down, so this link counts as down too.";
            }

            if (IsMonitored)
            {
                text += " LibreNMS monitors it - click to open it.";
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
            || IpText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || PortDescriptionText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || StateText.Contains(t, StringComparison.OrdinalIgnoreCase);
    }

    private static string Rate(double bps) => bps <= 0 ? "0 bps" : LinkUtilisation.Rate(bps);

    /// <summary>"0", "0.2/s", "8,633/s".</summary>
    private static string PerSecond(double value) =>
        value <= 0 ? "0" : value.ToString(value < 10 ? "0.#" : "N0", CultureInfo.CurrentCulture) + "/s";
}

/// <summary>One row in the Neighbours tab's table of views.</summary>
public sealed class NeighbourViewRowViewModel
{
    public NeighbourViewRowViewModel(NeighbourViewDefinition definition, string rulesText, int? matchCount)
    {
        Definition = definition;
        RulesText = rulesText;
        MatchCount = matchCount;
    }

    public NeighbourViewDefinition Definition { get; }

    public string Name => Definition.Name;

    public string RulesText { get; }

    /// <summary>How many neighbours it lists right now - null until they've loaded.</summary>
    public int? MatchCount { get; }

    public string MatchCountText => MatchCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "...";

    public string OnMapText => Definition.ShowOnMap ? "Yes" : "No";

    public bool Matches(string? term) =>
        string.IsNullOrWhiteSpace(term)
        || Name.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase)
        || RulesText.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);
}

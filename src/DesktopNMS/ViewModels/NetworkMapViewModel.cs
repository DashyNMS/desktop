using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One device on the network map. Position is mutable (layout, and the user
/// dragging it); everything else is refreshed from each device poll. Plain
/// data, not INotifyPropertyChanged: <see cref="Views.NetworkMapCanvas"/>
/// draws the whole map itself and redraws when told to.
/// </summary>
public sealed class MapNode
{
    public MapNode(int deviceId)
    {
        DeviceId = deviceId;
    }

    public int DeviceId { get; }

    public string Name { get; set; } = string.Empty;

    public DeviceState State { get; set; } = DeviceState.Down;

    /// <summary>Set for an access point (#55) rather than a LibreNMS device - its <see cref="DeviceId"/> is then a negative id of the map's own (see <see cref="AccessPoints.NodeId"/>).</summary>
    public AccessPoint? AccessPoint { get; init; }

    public bool IsAccessPoint => AccessPoint is not null;

    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>A connection on the map - one line between two nodes, however many cables it stands for.</summary>
public sealed class MapEdge
{
    public MapEdge(MapNode a, MapNode b, TopologyEdge source)
    {
        A = a;
        B = b;
        Source = source;
    }

    public MapNode A { get; }

    public MapNode B { get; }

    public TopologyEdge Source { get; }

    public int LinkCount => Source.LinkCount;

    /// <summary>
    /// Either end is down - drawn dotted, so a link to something that's
    /// gone offline reads differently from a working one. Maintenance and
    /// disabled devices aren't "down" here: the first is still up, and the
    /// second isn't polled, so there's no telling.
    /// </summary>
    public bool IsToOfflineDevice => A.State == DeviceState.Down || B.State == DeviceState.Down;

    public bool Touches(MapNode node) => ReferenceEquals(A, node) || ReferenceEquals(B, node);
}

/// <summary>An entry in the scope picker - the whole fleet, or one device group.</summary>
public sealed record MapScopeOption(string Key, string DisplayName, string? GroupName)
{
    public override string ToString() => DisplayName;
}

/// <summary>The "All devices / each device group" scope list both maps share.</summary>
public static class MapScopes
{
    public static MapScopeOption AllDevices { get; } = new("all", "All devices", null);

    /// <summary>
    /// Brings the list in line with the current group names (after
    /// <see cref="AllDevices"/>). Only touches it when the groups actually
    /// changed - clearing it would momentarily remove a selected group.
    /// Returns true if it changed, so the caller can re-select by key.
    /// </summary>
    public static bool Sync(ObservableCollection<MapScopeOption> scopes, IReadOnlyList<string> groupNames)
    {
        if (scopes.Skip(1).Select(s => s.GroupName).SequenceEqual(groupNames))
        {
            return false;
        }

        while (scopes.Count > 1)
        {
            scopes.RemoveAt(scopes.Count - 1);
        }

        foreach (var name in groupNames)
        {
            scopes.Add(new MapScopeOption("group:" + name, name, name));
        }

        return true;
    }
}

/// <summary>One line in the selected device's connection list: "Gi1/0/48 → r-sw-core-02 (1/1/1)".</summary>
public sealed class MapConnectionItem
{
    public MapConnectionItem(MapNode neighbour, string? localPort, string? remotePort)
    {
        Neighbour = neighbour;
        LocalPort = localPort ?? "?";
        RemotePort = remotePort ?? "?";
    }

    public MapNode Neighbour { get; }

    public string NeighbourName => Neighbour.Name;

    public string LocalPort { get; }

    public string RemotePort { get; }
}

/// <summary>
/// The Maps tab's Network map (issue #56): LibreNMS's discovered LLDP/CDP links drawn as a
/// network map, for the whole fleet or one device group. Nodes are coloured
/// by device up/down state from the shared <see cref="DeviceMonitor"/> poll;
/// links come from one fleet-wide <c>resources/links</c> call, re-fetched on
/// Refresh and when the tab is shown again after a while. Devices LibreNMS
/// monitors appear, plus the access points the switches see (see
/// <see cref="ShowAccessPoints"/>) - other neighbours it doesn't monitor,
/// such as phones, are left out. Positions are auto-laid-out, then remembered per scope (and per
/// server) once laid out or dragged, until Reset layout.
/// </summary>
public sealed class NetworkMapViewModel : ObservableObject, IDisposable
{
    /// <summary>How stale the link list can get before showing the tab again re-fetches it - links change far less often than device state.</summary>
    private static readonly TimeSpan LinksMaxAge = TimeSpan.FromMinutes(10);

    private readonly DeviceMonitor _deviceMonitor;
    private readonly ILibreNmsClient _client;
    private readonly IDeviceGroupMembershipService _groupMembership;
    private readonly IMapLayoutStore _layouts;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILogger<NetworkMapViewModel> _logger;
    private readonly IAccessPointDirectory _accessPointDirectory;
    private readonly Dispatcher _dispatcher;

    private IReadOnlyList<Device> _devices = Array.Empty<Device>();
    private IReadOnlySet<int> _maintenanceIds = new HashSet<int>();
    private IReadOnlyList<NetworkLink>? _links;
    private IReadOnlyDictionary<int, string>? _portNames;
    private DateTimeOffset _linksFetchedAt;
    private bool _hasDevices;

    private IReadOnlyList<MapNode> _nodes = Array.Empty<MapNode>();
    private IReadOnlyList<MapEdge> _edges = Array.Empty<MapEdge>();
    private MapScopeOption _selectedScope;
    private MapNode? _selectedNode;
    private bool _showUnlinkedDevices;
    private bool _showAccessPoints = true;
    private AccessPointSnapshot? _accessPoints;
    private string _searchText = string.Empty;
    private bool _isLoading;
    private string? _errorMessage;
    private int _renderVersion;

    /// <summary>Guards against an older layout (e.g. for a scope since switched away from) landing after a newer one.</summary>
    private int _buildVersion;

    public NetworkMapViewModel(
        DeviceMonitor deviceMonitor,
        ILibreNmsClient client,
        IDeviceGroupMembershipService groupMembership,
        IMapLayoutStore layouts,
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        IAccessPointDirectory accessPointDirectory,
        ILogger<NetworkMapViewModel> logger)
    {
        _deviceMonitor = deviceMonitor;
        _client = client;
        _groupMembership = groupMembership;
        _layouts = layouts;
        _session = session;
        _settings = settings;
        _windows = windows;
        _accessPointDirectory = accessPointDirectory;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _selectedScope = MapScopes.AllDevices;
        Scopes = new ObservableCollection<MapScopeOption> { _selectedScope };

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _session.IsConnected && !IsLoading);
        ResetLayoutCommand = new RelayCommand(ResetLayout);
        FitToViewCommand = new RelayCommand(() => FitToViewRequested?.Invoke(this, EventArgs.Empty));
        OpenSelectedDeviceCommand = new RelayCommand(OpenSelectedDevice);
        SelectNeighbourCommand = new RelayCommand(p =>
        {
            if (p is MapConnectionItem item)
            {
                SelectedNode = item.Neighbour;
                CenterOnRequested?.Invoke(this, item.Neighbour);
            }
        });
        ClearFiltersCommand = new RelayCommand(() => SearchText = string.Empty);

        _deviceMonitor.Polled += OnDevicesPolled;
        _groupMembership.Changed += OnGroupMembershipChanged;
        _session.StateChanged += OnSessionStateChanged;
    }

    /// <summary>Asks the view to fit the whole map in the window - after a new scope is laid out, or from the toolbar.</summary>
    public event EventHandler? FitToViewRequested;

    /// <summary>Asks the view to bring a node into the centre - search hits and neighbour clicks.</summary>
    public event EventHandler<MapNode>? CenterOnRequested;

    public ObservableCollection<MapScopeOption> Scopes { get; }

    public MapScopeOption SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (value is not null && SetProperty(ref _selectedScope, value))
            {
                SelectedNode = null;
                _ = RebuildAsync(fit: true);
            }
        }
    }

    public IReadOnlyList<MapNode> Nodes
    {
        get => _nodes;
        private set => SetProperty(ref _nodes, value);
    }

    public IReadOnlyList<MapEdge> Edges
    {
        get => _edges;
        private set => SetProperty(ref _edges, value);
    }

    /// <summary>Bumped whenever something the canvas draws changes without the collections themselves being replaced (states, names) - the canvas redraws on it.</summary>
    public int RenderVersion
    {
        get => _renderVersion;
        private set => SetProperty(ref _renderVersion, value);
    }

    public MapNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                OnPropertyChanged(nameof(HasSelectedNode));
                OnPropertyChanged(nameof(SelectedNodeStateText));
                OnPropertyChanged(nameof(SelectedNodeDetail));
                RebuildSelectedConnections();
            }
        }
    }

    public bool HasSelectedNode => _selectedNode is not null;

    public string SelectedNodeStateText => _selectedNode is { IsAccessPoint: true } ap
        ? ap.State switch
        {
            DeviceState.Up => "Access point - up",
            DeviceState.Down => "Access point - down",
            _ => "Access point",
        }
        : _selectedNode?.State switch
    {
        null => string.Empty,
        DeviceState.Up => "Up",
        DeviceState.Down => "Down",
        DeviceState.Maintenance => "In maintenance",
        DeviceState.Disabled => "Disabled",
        DeviceState.Ignored => "Ignored",
        _ => string.Empty,
    };

    /// <summary>"10.46.102.33 · Aruba JL320A" - IP and hardware for the details panel.</summary>
    public string SelectedNodeDetail
    {
        get
        {
            if (_selectedNode?.AccessPoint is { } ap)
            {
                return string.Join(" · ", new[] { ap.Model, ap.Mac }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            if (_selectedNode is null || _devices.FirstOrDefault(d => d.DeviceId == _selectedNode.DeviceId) is not { } device)
            {
                return string.Empty;
            }

            return string.Join(" · ", new[] { device.Ip, device.Hardware }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    public ObservableCollection<MapConnectionItem> SelectedConnections { get; } = new();

    /// <summary>Include devices in scope that have no links at all - off by default, since on a whole fleet they'd far outnumber the connected ones. Laid out in a grid below the connected map.</summary>
    public bool ShowUnlinkedDevices
    {
        get => _showUnlinkedDevices;
        set
        {
            if (SetProperty(ref _showUnlinkedDevices, value))
            {
                _ = RebuildAsync(fit: true);
            }
        }
    }

    /// <summary>
    /// Draw the access points the switches see over LLDP (#55), each joined
    /// to its switch - on by default. They aren't LibreNMS devices, so
    /// they're drawn smaller and square, coloured by their switch port's state.
    /// </summary>
    public bool ShowAccessPoints
    {
        get => _showAccessPoints;
        set
        {
            if (SetProperty(ref _showAccessPoints, value))
            {
                if (value && _accessPoints is null)
                {
                    _ = LoadAccessPointsAsync(refresh: false);
                }

                _ = RebuildAsync(fit: false);
            }
        }
    }

    /// <summary>Finds the first device whose name contains this and centres the map on it.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                FindSearchMatch();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                RaiseLoadingState();
            }
        }
    }

    private bool _isLayingOut;

    /// <summary>
    /// What the map is still waiting for before it's complete - null once
    /// everything's in. Devices, links and (for a group) group membership
    /// all arrive separately, then the layout runs; the map isn't worth
    /// showing until all of them have.
    /// </summary>
    public string? LoadingText =>
        !_session.IsConnected || HasError ? null :
        !_hasDevices ? "Loading devices..." :
        _isLoading || _links is null ? "Loading links..." :
        _selectedScope.GroupName is not null && !_groupMembership.HasLoaded ? "Loading device groups..." :
        _isLayingOut ? "Laying out the map..." :
        null;

    public bool ShowLoading => LoadingText is not null;

    private void RaiseLoadingState()
    {
        OnPropertyChanged(nameof(LoadingText));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(IsEmpty));
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

    /// <summary>Loaded, nothing to draw - e.g. a group whose members have no links between them.</summary>
    public bool IsEmpty => !ShowLoading && !HasError && _links is not null && _hasDevices && _nodes.Count == 0;

    /// <summary>"42 devices · 51 connections"</summary>
    public string SummaryText
    {
        get
        {
            if (_nodes.Count == 0)
            {
                return string.Empty;
            }

            var aps = _nodes.Count(n => n.IsAccessPoint);
            var devices = _nodes.Count - aps;
            var text = $"{devices} {(devices == 1 ? "device" : "devices")} · {_edges.Count} {(_edges.Count == 1 ? "connection" : "connections")}";
            return aps > 0 ? text + $" · {aps} {(aps == 1 ? "access point" : "access points")}" : text;
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ResetLayoutCommand { get; }

    public RelayCommand FitToViewCommand { get; }

    public RelayCommand OpenSelectedDeviceCommand { get; }

    public RelayCommand SelectNeighbourCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    public void OnShown()
    {
        _deviceMonitor.Start();
        _groupMembership.EnsureStarted();

        if (!_hasDevices)
        {
            _deviceMonitor.RequestRefresh();
        }

        if (_links is null || DateTimeOffset.Now - _linksFetchedAt > LinksMaxAge)
        {
            _ = LoadLinksAsync();

            if (_showAccessPoints)
            {
                _ = LoadAccessPointsAsync(refresh: false);
            }
        }
    }

    /// <summary>Called by the view after the user drops a dragged node - remembers the whole scope's layout.</summary>
    public void OnNodeMoved(MapNode node) => SaveLayout();

    /// <summary>Double-clicking a node - an access point opens on the Access points page.</summary>
    public void OpenDevice(MapNode node)
    {
        if (node.IsAccessPoint)
        {
            _windows.ShowAccessPoint(node.Name, node.AccessPoint?.Mac);
        }
        else
        {
            _windows.ShowDeviceDetail(node.DeviceId);
        }
    }

    private async Task RefreshAsync()
    {
        _deviceMonitor.RequestRefresh();
        _ = _groupMembership.RefreshAsync();
        if (_showAccessPoints)
        {
            _ = LoadAccessPointsAsync(refresh: true);
        }

        await LoadLinksAsync().ConfigureAwait(true);
    }

    /// <summary>The APs and their switch ports' state - shared with the Access points page. Not worth failing the map over.</summary>
    private async Task LoadAccessPointsAsync(bool refresh)
    {
        try
        {
            _accessPoints = await _accessPointDirectory.GetAsync(refresh).ConfigureAwait(true);
            if (_showAccessPoints)
            {
                await RebuildAsync(fit: false).ConfigureAwait(true);
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load access points for the network map");
        }
    }

    private async Task LoadLinksAsync()
    {
        if (!_session.IsConnected)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Port names are fetched alongside, so each end of a link can be
            // named from its own port - see NetworkTopology.Build.
            var portsTask = LoadPortNamesAsync();
            _links = await _client.Links.ListAllAsync().ConfigureAwait(true);
            _portNames = await portsTask.ConfigureAwait(true);
            _linksFetchedAt = DateTimeOffset.Now;
            await RebuildAsync(fit: _nodes.Count == 0).ConfigureAwait(true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load network links for the map");
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Every port's name by id. Not worth failing the map over - without
    /// them, a link reported from one side only just shows "?" for that
    /// side's own port, as it did before.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>?> LoadPortNamesAsync()
    {
        try
        {
            var ports = await _client.Ports.ListAllNamesAsync().ConfigureAwait(false);
            var names = new Dictionary<int, string>(ports.Count);
            foreach (var port in ports)
            {
                if (PortLabels.ForPort(port) is { } name)
                {
                    names[port.PortId] = name;
                }
            }

            return names;
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load port names for the network map; one-sided links will show ? for their own port");
            return null;
        }
    }

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            var previousIds = _devices.Select(d => d.DeviceId).ToHashSet();
            _devices = result.Devices;
            _maintenanceIds = result.DeviceIdsUnderMaintenance;
            _hasDevices = true;
            RaiseLoadingState();

            // A device added or removed changes the graph itself; otherwise
            // just repaint states and names in place, keeping the layout.
            if (!previousIds.SetEquals(_devices.Select(d => d.DeviceId)))
            {
                _ = RebuildAsync(fit: _nodes.Count == 0);
            }
            else
            {
                ApplyDeviceDetails();
            }
        });
    }

    private void OnGroupMembershipChanged(object? sender, EventArgs e)
    {
        var current = _selectedScope;

        if (MapScopes.Sync(Scopes, _groupMembership.GroupNames))
        {
            // Keep the selection by key where that group still exists.
            _selectedScope = Scopes.FirstOrDefault(s => s.Key == current.Key) ?? Scopes[0];
            OnPropertyChanged(nameof(SelectedScope));
        }

        // Membership decides which devices a group's map covers.
        if (_selectedScope.GroupName is not null)
        {
            _ = RebuildAsync(fit: current.Key != _selectedScope.Key || _nodes.Count == 0);
        }

        RaiseLoadingState();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() =>
    {
        if (!_session.IsConnected)
        {
            _links = null;
            _portNames = null;
            _accessPoints = null;
            _devices = Array.Empty<Device>();
            _hasDevices = false;
            SelectedNode = null;
            Nodes = Array.Empty<MapNode>();
            Edges = Array.Empty<MapEdge>();
            OnPropertyChanged(nameof(SummaryText));
        }

        RaiseLoadingState();
    });

    /// <summary>
    /// Rebuilds the graph for the current scope and lays out whatever isn't
    /// already positioned. Layout runs off the UI thread (it's O(n²) per
    /// step); a newer rebuild started meanwhile wins.
    /// </summary>
    private async Task RebuildAsync(bool fit)
    {
        if (_links is null || !_hasDevices)
        {
            return;
        }

        var version = ++_buildVersion;
        var scope = _selectedScope;

        if (scope.GroupName is not null && !_groupMembership.HasLoaded)
        {
            // Without membership a group would lay out as empty; clear the
            // previous scope's map and wait - OnGroupMembershipChanged
            // rebuilds once it's in, and the loading overlay says so meanwhile.
            Nodes = Array.Empty<MapNode>();
            Edges = Array.Empty<MapEdge>();
            _isLayingOut = false;
            RaiseLoadingState();
            return;
        }
        var scopeIds = scope.GroupName is { } group
            ? _devices.Where(d => _groupMembership.GroupsFor(d.DeviceId).Contains(group, StringComparer.OrdinalIgnoreCase)).Select(d => d.DeviceId)
            : _devices.Select(d => d.DeviceId);

        var graph = NetworkTopology.Build(scopeIds, _links, _portNames);

        // Access points (#55): one node each, joined to the switches in
        // scope they're plugged into - which makes those switches linked,
        // even with nothing else connected to them.
        var scopeSet = graph.DeviceIds.ToHashSet();
        var accessPoints = _showAccessPoints && _accessPoints is { } snapshot
            ? snapshot.AccessPoints.Where(ap => scopeSet.Contains(ap.SwitchDeviceId)).ToList()
            : new List<AccessPoint>();
        var apEdges = AccessPoints.MapEdges(accessPoints, _portNames);
        var apNodes = accessPoints
            .GroupBy(AccessPoints.NodeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allEdges = graph.Edges.Concat(apEdges).ToList();
        var linkedIds = graph.DeviceIds.Except(graph.UnlinkedDeviceIds)
            .Concat(apEdges.Select(e => e.DeviceB))
            .Distinct()
            .Concat(apNodes.Keys)
            .ToList();
        var linkedSet = linkedIds.ToHashSet();
        var unlinkedIds = _showUnlinkedDevices ? graph.UnlinkedDeviceIds.Where(id => !linkedSet.Contains(id)).ToList() : new List<int>();
        var saved = _layouts.Get(LayoutKey(scope));
        var edgePairs = allEdges.Select(e => (e.DeviceA, e.DeviceB)).ToList();

        _isLayingOut = true;
        RaiseLoadingState();

        var positions = await Task.Run(() =>
        {
            var laidOut = ForceDirectedLayout.Compute(linkedIds, edgePairs, Pinned(saved, linkedIds));

            var gridIds = unlinkedIds.Where(id => !saved.ContainsKey(id)).ToList();
            foreach (var (id, point) in ForceDirectedLayout.Grid(gridIds, laidOut.Values.ToList()))
            {
                laidOut[id] = point;
            }

            foreach (var id in unlinkedIds.Where(saved.ContainsKey))
            {
                laidOut[id] = saved[id];
            }

            return laidOut;
        }).ConfigureAwait(true);

        if (version != _buildVersion)
        {
            // Superseded - the newer rebuild owns the loading state now.
            return;
        }

        _isLayingOut = false;

        var nodes = new Dictionary<int, MapNode>();
        foreach (var id in linkedIds.Concat(unlinkedIds))
        {
            var point = positions[id];
            nodes[id] = apNodes.TryGetValue(id, out var aps)
                ? new MapNode(id) { X = point.X, Y = point.Y, AccessPoint = aps[0], Name = aps[0].Name, State = AccessPointState(aps) }
                : new MapNode(id) { X = point.X, Y = point.Y };
        }

        var edges = allEdges.Select(e => new MapEdge(nodes[e.DeviceA], nodes[e.DeviceB], e)).ToList();

        var selectedId = _selectedNode?.DeviceId;
        Nodes = nodes.Values.ToList();
        Edges = edges;
        ApplyDeviceDetails();
        SelectedNode = selectedId is { } sid && nodes.TryGetValue(sid, out var reselect) ? reselect : null;

        OnPropertyChanged(nameof(SummaryText));
        RaiseLoadingState();

        // Remember the result so it stays put from now on - the first layout
        // for a scope included.
        SaveLayout();

        if (fit)
        {
            FitToViewRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>An AP node's colour: up if any of its switch ports is up, down if one is down, otherwise unknown (grey).</summary>
    private DeviceState AccessPointState(IReadOnlyList<AccessPoint> aps)
    {
        var states = aps
            .Select(ap => new AccessPointItemViewModel(ap, _accessPoints?.PortOf(ap), _devices.FirstOrDefault(d => d.DeviceId == ap.SwitchDeviceId)).State)
            .ToList();

        return states.Contains(ViewModels.AccessPointState.Up) ? DeviceState.Up
            : states.Contains(ViewModels.AccessPointState.Down) ? DeviceState.Down
            : DeviceState.Disabled;
    }

    private static Dictionary<int, MapPoint> Pinned(IReadOnlyDictionary<int, MapPoint> saved, IEnumerable<int> ids) =>
        ids.Where(saved.ContainsKey).ToDictionary(id => id, id => saved[id]);

    /// <summary>Names and up/down state from the latest device poll, onto the existing nodes.</summary>
    private void ApplyDeviceDetails()
    {
        var byId = _devices.ToDictionary(d => d.DeviceId);
        var nameStyle = _settings.Current.DeviceNameStyle;

        foreach (var node in _nodes)
        {
            if (byId.TryGetValue(node.DeviceId, out var device))
            {
                node.Name = nameStyle.Resolve(device, device.Hostname);
                node.State = _maintenanceIds.Contains(node.DeviceId) ? DeviceState.Maintenance : device.State;
            }
            else if (node.IsAccessPoint && _accessPoints is { } snapshot)
            {
                // An AP follows its switch - down with it, see AccessPointItemViewModel.
                node.State = AccessPointState(snapshot.AccessPoints.Where(ap => AccessPoints.NodeId(ap) == node.DeviceId).ToList());
            }
        }

        RenderVersion++;
        OnPropertyChanged(nameof(SelectedNodeStateText));
        OnPropertyChanged(nameof(SelectedNodeDetail));
        RebuildSelectedConnections();
    }

    private void RebuildSelectedConnections()
    {
        SelectedConnections.Clear();

        if (_selectedNode is not { } node)
        {
            return;
        }

        foreach (var edge in _edges.Where(e => e.Touches(node)).OrderBy(e => (ReferenceEquals(e.A, node) ? e.B : e.A).Name, StringComparer.OrdinalIgnoreCase))
        {
            var isA = ReferenceEquals(edge.A, node);
            var neighbour = isA ? edge.B : edge.A;

            foreach (var connection in edge.Source.Connections)
            {
                SelectedConnections.Add(new MapConnectionItem(
                    neighbour,
                    isA ? connection.PortA : connection.PortB,
                    isA ? connection.PortB : connection.PortA));
            }
        }
    }

    private void ResetLayout()
    {
        if (_nodes.Count == 0)
        {
            return;
        }

        if (!_windows.Confirm("Reset layout", $"Forget the saved positions for \"{_selectedScope.DisplayName}\" and lay the map out again?"))
        {
            return;
        }

        _layouts.Clear(LayoutKey(_selectedScope));
        _ = RebuildAsync(fit: true);
    }

    private void SaveLayout()
    {
        if (_nodes.Count == 0)
        {
            return;
        }

        // Merged over what's already saved, so positions of devices not
        // currently drawn (e.g. unlinked ones with the toggle off) survive.
        var key = LayoutKey(_selectedScope);
        var merged = new Dictionary<int, MapPoint>(_layouts.Get(key));
        foreach (var node in _nodes)
        {
            merged[node.DeviceId] = new MapPoint(node.X, node.Y);
        }

        _layouts.Save(key, merged);
    }

    /// <summary>Per server as well as per scope - device ids mean nothing on a different LibreNMS.</summary>
    private string LayoutKey(MapScopeOption scope) =>
        $"{_session.Connection?.WebRoot.Host ?? "unknown"}|{scope.Key}";

    private void OpenSelectedDevice()
    {
        if (_selectedNode is { } node)
        {
            OpenDevice(node);
        }
    }

    private void FindSearchMatch()
    {
        var term = _searchText.Trim();
        if (term.Length == 0)
        {
            return;
        }

        var match = _nodes
            .Where(n => n.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n.Name.Length)
            .FirstOrDefault();

        if (match is not null)
        {
            SelectedNode = match;
            CenterOnRequested?.Invoke(this, match);
        }
    }

    public void Dispose()
    {
        _deviceMonitor.Polled -= OnDevicesPolled;
        _groupMembership.Changed -= OnGroupMembershipChanged;
        _session.StateChanged -= OnSessionStateChanged;
    }
}

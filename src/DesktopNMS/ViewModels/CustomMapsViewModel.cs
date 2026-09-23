using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DesktopNMS.ViewModels;

/// <summary>How a node is drawn right now - its own colours, or the down/disabled styling its device's state calls for.</summary>
public sealed record CustomMapNodeVisual(string Background, string Border, string Text);

/// <summary>How each half of a link is drawn right now: "from" is node 1 → midpoint, "to" is node 2 → midpoint.</summary>
public sealed record CustomMapEdgeVisual(string ColourFrom, string ColourTo, double WidthFrom, double WidthTo, string LabelFrom, string LabelTo);

/// <summary>Everything live the canvas needs on top of the map document itself.</summary>
public sealed record CustomMapVisuals(
    IReadOnlyDictionary<string, CustomMapNodeVisual> Nodes,
    IReadOnlyDictionary<string, CustomMapEdgeVisual> Edges)
{
    public static CustomMapVisuals Empty { get; } = new(new Dictionary<string, CustomMapNodeVisual>(), new Dictionary<string, CustomMapEdgeVisual>());
}

/// <summary>What clicking on the map does while editing.</summary>
public enum CustomMapTool
{
    Select,
    AddNode,
    AddLink,
}

/// <summary>
/// The Maps tab's Custom Maps: hand-drawn maps of devices and links, the
/// same idea (and options) as LibreNMS's own Custom Maps, kept locally as
/// one ".map" file each (see <see cref="ICustomMapStore"/>) that can be
/// exported and imported. Viewing shows live device state and link
/// utilisation; editing works on a copy until Save.
/// </summary>
public sealed class CustomMapsViewModel : ObservableObject, IDisposable
{
    /// <summary>Port traffic is re-read at most this often while a map with port-linked links is showing.</summary>
    private static readonly TimeSpan PortRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>LibreNMS's network_map_legend "dn" (down) and "di" (disabled) node styling.</summary>
    private static readonly CustomMapNodeVisual DownStyle = new("#FFDDDD", "#FF5555", "#8B0000");
    private static readonly CustomMapNodeVisual DisabledStyle = new("#EEEEEE", "#CCCCCC", "#343434");

    /// <summary>Colour of a link with no port - just a line.</summary>
    private const string PlainLinkColour = "#7D8590";

    private readonly ICustomMapStore _store;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILogger<CustomMapsViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    private IReadOnlyList<Device> _devices = Array.Empty<Device>();
    private IReadOnlySet<int> _maintenanceIds = new HashSet<int>();
    private readonly Dictionary<int, Port> _ports = new();
    private readonly Dictionary<int, IReadOnlyList<Port>> _portsByDevice = new();
    private DateTimeOffset _portsFetchedAt;
    private bool _isFetchingPorts;

    private CustomMapSummary? _selectedSummary;
    private CustomMapDocument? _map;
    private CustomMapDocument? _savedMap;
    private bool _isEditing;
    private bool _isDirty;
    private CustomMapTool _tool;
    private string? _pendingLinkNodeId;
    private string? _selectedNodeId;
    private string? _selectedEdgeId;
    private object? _editor;
    private CustomMapVisuals _visuals = CustomMapVisuals.Empty;
    private int _renderVersion;
    private bool _suppressSelectionLoad;

    public CustomMapsViewModel(
        ICustomMapStore store,
        DeviceMonitor deviceMonitor,
        ILibreNmsClient client,
        IMapTileService tiles,
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        ILogger<CustomMapsViewModel> logger)
    {
        _store = store;
        _deviceMonitor = deviceMonitor;
        _client = client;
        Tiles = tiles;
        _session = session;
        _settings = settings;
        _windows = windows;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Maps = new ObservableCollection<CustomMapSummary>();
        MapsView = CollectionViewSource.GetDefaultView(Maps);
        MapsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CustomMapSummary.MenuGroup)));

        NewMapCommand = new RelayCommand(NewMap);
        ImportMapCommand = new RelayCommand(ImportMap);
        ExportMapCommand = new RelayCommand(ExportMap, () => _map is not null);
        DuplicateMapCommand = new RelayCommand(DuplicateMap, () => _map is not null && !_isEditing);
        DeleteMapCommand = new RelayCommand(DeleteMap, () => _map is not null && !_isEditing);
        EditMapCommand = new RelayCommand(StartEditing, () => _map is not null && !_isEditing);
        SaveMapCommand = new RelayCommand(SaveEdits, () => _isEditing);
        CancelEditCommand = new RelayCommand(CancelEditing, () => _isEditing);
        SelectToolCommand = new RelayCommand(p => Tool = p is CustomMapTool t ? t : Enum.TryParse<CustomMapTool>(p as string, out var parsed) ? parsed : CustomMapTool.Select);
        DeleteSelectionCommand = new RelayCommand(DeleteSelection, () => _isEditing && (_selectedNodeId is not null || _selectedEdgeId is not null));
        RecentreLinksCommand = new RelayCommand(RecentreLinks, () => _isEditing);
        UseAsNodeDefaultCommand = new RelayCommand(UseAsNodeDefault);
        UseAsLinkDefaultCommand = new RelayCommand(UseAsLinkDefault);
        ChooseNodeImageCommand = new RelayCommand(ChooseNodeImage);
        ChooseBackgroundImageCommand = new RelayCommand(ChooseBackgroundImage);
        OpenSelectedCommand = new RelayCommand(OpenSelected);

        _store.Changed += OnStoreChanged;
        _deviceMonitor.Polled += OnDevicesPolled;
        _session.StateChanged += OnSessionStateChanged;

        ReloadList();
    }

    /// <summary>Asks the view to fit the whole map in the window - after opening one.</summary>
    public event EventHandler? FitToViewRequested;

    public IMapTileService Tiles { get; }

    /// <summary>The tile server for a "geographic map" background - the same one the Geographical map uses (Settings → Maps).</summary>
    public string TileTemplate => Core.Topology.TileUrlTemplate.Normalise(_settings.Current.MapTileUrl) ?? Core.Topology.TileUrlTemplate.Default;

    public ObservableCollection<CustomMapSummary> Maps { get; }

    /// <summary>The map list grouped by menu group, like LibreNMS's Custom Maps menu.</summary>
    public ICollectionView MapsView { get; }

    public bool HasMaps => Maps.Count > 0;

    public CustomMapSummary? SelectedSummary
    {
        get => _selectedSummary;
        set
        {
            if (ReferenceEquals(value, _selectedSummary) || _suppressSelectionLoad)
            {
                SetProperty(ref _selectedSummary, value);
                return;
            }

            if (_isEditing && _isDirty && !_windows.Confirm("Discard changes", $"Discard your unsaved changes to \"{_map?.Name}\"?"))
            {
                // Put the list selection back on the map still being edited.
                _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedSummary)));
                return;
            }

            SetProperty(ref _selectedSummary, value);
            Open(value?.Id);
        }
    }

    /// <summary>The map on screen - the working copy while editing.</summary>
    public CustomMapDocument? Map
    {
        get => _map;
        private set
        {
            if (SetProperty(ref _map, value))
            {
                OnPropertyChanged(nameof(HasMap));
                RaiseCommands();
            }
        }
    }

    public bool HasMap => _map is not null;

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(IsViewing));
                RaiseCommands();
                RebuildVisuals();
            }
        }
    }

    public bool IsViewing => !_isEditing;

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(TitleText));
            }
        }
    }

    public string TitleText => _map is null ? string.Empty : _isDirty ? _map.Name + " •" : _map.Name;

    public CustomMapTool Tool
    {
        get => _tool;
        set
        {
            if (SetProperty(ref _tool, value))
            {
                _pendingLinkNodeId = null;
                OnPropertyChanged(nameof(IsSelectTool));
                OnPropertyChanged(nameof(IsAddNodeTool));
                OnPropertyChanged(nameof(IsAddLinkTool));
                OnPropertyChanged(nameof(ToolHint));
            }
        }
    }

    public bool IsSelectTool => _tool == CustomMapTool.Select;

    public bool IsAddNodeTool => _tool == CustomMapTool.AddNode;

    public bool IsAddLinkTool => _tool == CustomMapTool.AddLink;

    public string ToolHint => _tool switch
    {
        CustomMapTool.AddNode => "Click on the map to place a node.",
        CustomMapTool.AddLink when _pendingLinkNodeId is not null => "Now click the node to link it to.",
        CustomMapTool.AddLink => "Click the first node of the link.",
        _ => "Drag nodes, link midpoints and the legend to arrange them. Click something to edit it.",
    };

    public string? SelectedNodeId
    {
        get => _selectedNodeId;
        private set => SetProperty(ref _selectedNodeId, value);
    }

    public string? SelectedEdgeId
    {
        get => _selectedEdgeId;
        private set => SetProperty(ref _selectedEdgeId, value);
    }

    /// <summary>
    /// What the right-hand panel edits: a node, a link, or (nothing
    /// selected) the map's own settings. Null while only viewing.
    /// </summary>
    public object? Editor
    {
        get => _editor;
        private set
        {
            if (SetProperty(ref _editor, value))
            {
                OnPropertyChanged(nameof(HasEditor));
            }
        }
    }

    public bool HasEditor => _editor is not null;

    /// <summary>While viewing: the clicked device node, for the details panel.</summary>
    public string? ViewSelectionName { get; private set; }

    public string? ViewSelectionState { get; private set; }

    public bool HasViewSelection => !_isEditing && ViewSelectionName is not null;

    public CustomMapVisuals Visuals
    {
        get => _visuals;
        private set => SetProperty(ref _visuals, value);
    }

    public int RenderVersion
    {
        get => _renderVersion;
        private set => SetProperty(ref _renderVersion, value);
    }

    public RelayCommand NewMapCommand { get; }

    public RelayCommand ImportMapCommand { get; }

    public RelayCommand ExportMapCommand { get; }

    public RelayCommand DuplicateMapCommand { get; }

    public RelayCommand DeleteMapCommand { get; }

    public RelayCommand EditMapCommand { get; }

    public RelayCommand SaveMapCommand { get; }

    public RelayCommand CancelEditCommand { get; }

    public RelayCommand SelectToolCommand { get; }

    public RelayCommand DeleteSelectionCommand { get; }

    public RelayCommand RecentreLinksCommand { get; }

    public RelayCommand UseAsNodeDefaultCommand { get; }

    public RelayCommand UseAsLinkDefaultCommand { get; }

    public RelayCommand ChooseNodeImageCommand { get; }

    public RelayCommand ChooseBackgroundImageCommand { get; }

    public RelayCommand OpenSelectedCommand { get; }

    public void OnShown()
    {
        _deviceMonitor.Start();
        if (_devices.Count == 0)
        {
            _deviceMonitor.RequestRefresh();
        }

        _ = RefreshPortsAsync(force: true);
    }

    /// <summary>Opens a specific map - the Default map setting, or a node's "links to map".</summary>
    public void OpenMap(string id)
    {
        var summary = Maps.FirstOrDefault(m => m.Id == id);
        if (summary is not null)
        {
            SelectedSummary = summary;
        }
    }

    public Task RefreshAsync()
    {
        ReloadList();
        _deviceMonitor.RequestRefresh();
        return RefreshPortsAsync(force: true);
    }

    // ------------------------------------------------------------ canvas input

    /// <summary>A click on empty map space, in map coordinates.</summary>
    public void OnBackgroundClicked(double x, double y)
    {
        if (!_isEditing || _map is null)
        {
            ClearViewSelection();
            return;
        }

        if (_tool == CustomMapTool.AddNode)
        {
            var node = _map.NodeDefaults.CopyStyle();
            node.Label = "New node";
            (node.X, node.Y) = Snap(x, y);
            _map.Nodes.Add(node);
            MarkChanged();
            Select(node.Id, null);

            // One node per click of the tool - back to selecting, so the
            // next click edits rather than adding again.
            Tool = CustomMapTool.Select;
            return;
        }

        _pendingLinkNodeId = null;
        OnPropertyChanged(nameof(ToolHint));
        Select(null, null);
    }

    public void OnNodeClicked(string nodeId)
    {
        if (_map is null)
        {
            return;
        }

        if (!_isEditing)
        {
            ShowViewSelection(nodeId);
            return;
        }

        if (_tool == CustomMapTool.AddLink)
        {
            if (_pendingLinkNodeId is null)
            {
                _pendingLinkNodeId = nodeId;
                OnPropertyChanged(nameof(ToolHint));
                return;
            }

            if (_pendingLinkNodeId != nodeId)
            {
                var a = _map.Nodes.First(n => n.Id == _pendingLinkNodeId);
                var b = _map.Nodes.First(n => n.Id == nodeId);
                var edge = _map.EdgeDefaults.CopyStyle();
                edge.Node1Id = a.Id;
                edge.Node2Id = b.Id;
                edge.MidX = (a.X + b.X) / 2;
                edge.MidY = (a.Y + b.Y) / 2;
                _map.Edges.Add(edge);
                MarkChanged();
                Tool = CustomMapTool.Select;
                Select(null, edge.Id);
            }

            return;
        }

        Select(nodeId, null);
    }

    public void OnEdgeClicked(string edgeId)
    {
        if (_isEditing && _tool == CustomMapTool.Select)
        {
            Select(null, edgeId);
        }
    }

    /// <summary>Double-click while viewing: open the node's device, or jump to the map it links to.</summary>
    public void OnNodeActivated(string nodeId)
    {
        if (_isEditing || _map?.Nodes.FirstOrDefault(n => n.Id == nodeId) is not { } node)
        {
            return;
        }

        if (node.LinkedMapId is { } linked && Maps.Any(m => m.Id == linked))
        {
            OpenMap(linked);
        }
        else if (node.DeviceId is { } deviceId)
        {
            _windows.ShowDeviceDetail(deviceId);
        }
    }

    /// <summary>After a drag in the editor (node, midpoint or legend) - the canvas has already moved it in the working copy.</summary>
    public void OnMoved() => MarkChanged();

    /// <summary>Grid snapping for node placement - the map's NodeAlign.</summary>
    public (double X, double Y) Snap(double x, double y)
    {
        var grid = _map?.NodeAlign ?? 0;
        return grid > 0 ? (Math.Round(x / grid) * grid, Math.Round(y / grid) * grid) : (x, y);
    }

    // ------------------------------------------------------------- list/files

    private void ReloadList()
    {
        var currentId = _selectedSummary?.Id;

        _suppressSelectionLoad = true;
        Maps.Clear();
        foreach (var summary in _store.List())
        {
            Maps.Add(summary);
        }

        SelectedSummary = currentId is null ? null : Maps.FirstOrDefault(m => m.Id == currentId);
        _suppressSelectionLoad = false;
        OnPropertyChanged(nameof(HasMaps));
    }

    private void OnStoreChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(ReloadList);

    private void Open(string? id)
    {
        IsEditing = false;
        IsDirty = false;
        Tool = CustomMapTool.Select;
        Select(null, null);
        ClearViewSelection();

        var map = id is null ? null : _store.Load(id);
        _savedMap = map;
        Map = map;
        RebuildVisuals();
        _ = RefreshPortsAsync(force: true);

        if (map is not null)
        {
            FitToViewRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NewMap()
    {
        if (_isEditing && _isDirty && !_windows.Confirm("Discard changes", $"Discard your unsaved changes to \"{_map?.Name}\"?"))
        {
            return;
        }

        var map = new CustomMapDocument { Name = UniqueName("New map") };
        _store.Save(map);
        ReloadList();
        OpenMap(map.Id);
        StartEditing();

        // Nothing's selected yet, so the panel shows the map's own settings -
        // name it first.
        Select(null, null);
    }

    private void ImportMap()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import custom map",
            Filter = "DashyNMS maps (*.map)|*.map|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var imported = _store.Import(dialog.FileName);
            ReloadList();
            OpenMap(imported.Id);
        }
        catch (Exception ex) when (ex is CustomMapFormatException or IOException or UnauthorizedAccessException)
        {
            _windows.ShowError("Import failed", ex.Message);
        }
    }

    private void ExportMap()
    {
        if (_map is null)
        {
            return;
        }

        if (_isEditing && _isDirty)
        {
            _windows.ShowInformation("Save first", "Save your changes before exporting, so the exported map includes them.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export custom map",
            Filter = "DashyNMS maps (*.map)|*.map",
            FileName = SafeFileName(_map.Name) + CustomMapSerializer.FileExtension,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _store.Export(_map.Id, dialog.FileName);
        }
        catch (Exception ex) when (ex is CustomMapFormatException or IOException or UnauthorizedAccessException)
        {
            _windows.ShowError("Export failed", ex.Message);
        }
    }

    private void DuplicateMap()
    {
        if (_map is null)
        {
            return;
        }

        var copy = _map.Clone();
        copy.Id = CustomMapDocument.NewId();
        copy.Name = UniqueName(_map.Name + " (copy)");
        _store.Save(copy);
        ReloadList();
        OpenMap(copy.Id);
    }

    private void DeleteMap()
    {
        if (_map is null || !_windows.Confirm("Delete map", $"Delete \"{_map.Name}\"? This can't be undone - export it first if you might want it back."))
        {
            return;
        }

        var id = _map.Id;
        _suppressSelectionLoad = true;
        SelectedSummary = null;
        _suppressSelectionLoad = false;
        Open(null);
        _store.Delete(id);
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; Maps.Any(m => string.Equals(m.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name} {i}";
        }

        return candidate;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "map" : safe;
    }

    // ---------------------------------------------------------------- editing

    private void StartEditing()
    {
        if (_map is null)
        {
            return;
        }

        // A copy, so Cancel just drops it.
        Map = _map.Clone();
        IsDirty = false;
        IsEditing = true;
        ClearViewSelection();
        Select(null, null);
    }

    private void SaveEdits()
    {
        if (_map is null)
        {
            return;
        }

        try
        {
            // The working copy becomes the saved map; editing again starts a
            // fresh copy of it.
            _store.Save(_map);
            _savedMap = _map;
            IsDirty = false;
            IsEditing = false;
            Tool = CustomMapTool.Select;
            Select(null, null);
            _ = RefreshPortsAsync(force: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _windows.ShowError("Save failed", ex.Message);
        }
    }

    private void CancelEditing()
    {
        if (_isDirty && !_windows.Confirm("Discard changes", "Discard your unsaved changes?"))
        {
            return;
        }

        Map = _savedMap;
        IsDirty = false;
        IsEditing = false;
        Tool = CustomMapTool.Select;
        Select(null, null);
    }

    private void Select(string? nodeId, string? edgeId)
    {
        SelectedNodeId = nodeId;
        SelectedEdgeId = edgeId;
        DeleteSelectionCommand.RaiseCanExecuteChanged();

        if (!_isEditing || _map is null)
        {
            Editor = null;
            return;
        }

        if (nodeId is not null && _map.Nodes.FirstOrDefault(n => n.Id == nodeId) is { } node)
        {
            Editor = new CustomMapNodeEditor(node, DeviceOptions(), LinkOptions(), MarkChanged);
        }
        else if (edgeId is not null && _map.Edges.FirstOrDefault(e => e.Id == edgeId) is { } edge)
        {
            var editor = new CustomMapEdgeEditor(edge, EndsText(edge), PortOptions(edge), MarkChanged);
            Editor = editor;
            _ = LoadPortsForEdgeAsync(edge, editor);
        }
        else
        {
            Editor = new CustomMapSettingsEditor(_map, MarkChanged);
        }
    }

    private void DeleteSelection()
    {
        if (_map is null)
        {
            return;
        }

        if (_selectedNodeId is { } nodeId)
        {
            _map.Nodes.RemoveAll(n => n.Id == nodeId);
            _map.Edges.RemoveAll(e => e.Node1Id == nodeId || e.Node2Id == nodeId);
        }
        else if (_selectedEdgeId is { } edgeId)
        {
            _map.Edges.RemoveAll(e => e.Id == edgeId);
        }

        Select(null, null);
        MarkChanged();
    }

    /// <summary>LibreNMS's "Re-Render Map": puts every link's midpoint back halfway between its nodes, straightening bent links.</summary>
    private void RecentreLinks()
    {
        if (_map is null)
        {
            return;
        }

        var nodes = _map.Nodes.ToDictionary(n => n.Id);
        foreach (var edge in _map.Edges)
        {
            var a = nodes[edge.Node1Id];
            var b = nodes[edge.Node2Id];
            edge.MidX = (a.X + b.X) / 2;
            edge.MidY = (a.Y + b.Y) / 2;
        }

        MarkChanged();
    }

    private void UseAsNodeDefault()
    {
        if (_map is not null && _editor is CustomMapNodeEditor editor)
        {
            _map.NodeDefaults = editor.Node.CopyStyle();
            MarkChanged();
            _windows.ShowInformation("Node defaults", "New nodes on this map will now start with this node's look.");
        }
    }

    private void UseAsLinkDefault()
    {
        if (_map is not null && _editor is CustomMapEdgeEditor editor)
        {
            _map.EdgeDefaults = editor.Edge.CopyStyle();
            MarkChanged();
            _windows.ShowInformation("Link defaults", "New links on this map will now start with this link's settings.");
        }
    }

    private void ChooseNodeImage()
    {
        if (_map is null || _editor is not CustomMapNodeEditor editor || ChooseImage() is not { } image)
        {
            return;
        }

        var id = CustomMapDocument.NewId();
        _map.Images[id] = image;
        editor.Node.ImageId = id;
        MarkChanged();
    }

    private void ChooseBackgroundImage()
    {
        if (_map is null || ChooseImage() is not { } image)
        {
            return;
        }

        var id = CustomMapDocument.NewId();
        _map.Images[id] = image;
        _map.Background.ImageId = id;
        _map.Background.Type = CustomMapBackgroundType.Image;
        MarkChanged();
        Select(null, null);
    }

    /// <summary>An image to embed in the map - PNG, JPEG, GIF or BMP, up to 5 MB, as LibreNMS limits its uploads.</summary>
    private CustomMapImage? ChooseImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp",
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var info = new FileInfo(dialog.FileName);
        if (info.Length > 5 * 1024 * 1024)
        {
            _windows.ShowError("Image too large", "Choose an image under 5 MB - it's stored inside the map file.");
            return null;
        }

        var mime = info.Extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };

        return new CustomMapImage { MimeType = mime, Data = Convert.ToBase64String(File.ReadAllBytes(dialog.FileName)) };
    }

    private void MarkChanged()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(TitleText));
        RebuildVisuals();
        RenderVersion++;
    }

    private IReadOnlyList<MapDeviceOption> DeviceOptions()
    {
        var nameStyle = _settings.Current.DeviceNameStyle;
        return new[] { new MapDeviceOption(null, "(none)") }
            .Concat(_devices
                .Select(d => new MapDeviceOption(d.DeviceId, nameStyle.Resolve(d, d.Hostname)))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private IReadOnlyList<MapLinkOption> LinkOptions() =>
        new[] { new MapLinkOption(null, "(none)") }
            .Concat(Maps.Where(m => m.Id != _map?.Id).Select(m => new MapLinkOption(m.Id, m.Name)))
            .ToList();

    private string EndsText(CustomMapEdge edge)
    {
        string Name(string id) => _map?.Nodes.FirstOrDefault(n => n.Id == id)?.Label is { Length: > 0 } label ? label : "(unnamed)";
        return $"{Name(edge.Node1Id)} ↔ {Name(edge.Node2Id)}";
    }

    /// <summary>"(none)" plus every port on either end's device, as LibreNMS's link editor offers.</summary>
    private IReadOnlyList<MapPortOption> PortOptions(CustomMapEdge edge)
    {
        var options = new List<MapPortOption> { new(null, "(none - plain line)") };

        foreach (var nodeId in new[] { edge.Node1Id, edge.Node2Id })
        {
            if (_map?.Nodes.FirstOrDefault(n => n.Id == nodeId) is { DeviceId: { } deviceId } node
                && _portsByDevice.TryGetValue(deviceId, out var ports))
            {
                options.AddRange(ports
                    .Where(p => !p.Deleted)
                    .OrderBy(p => p.IfIndex ?? int.MaxValue)
                    .Select(p => new MapPortOption(p.PortId, $"{node.Label}: {p.DisplayName}")));
            }
        }

        // Keep a port the map already uses selectable even before its
        // device's ports have loaded.
        if (edge.PortId is { } portId && options.All(o => o.PortId != portId))
        {
            options.Add(new MapPortOption(portId, $"Port #{portId}"));
        }

        return options;
    }

    private async Task LoadPortsForEdgeAsync(CustomMapEdge edge, CustomMapEdgeEditor editor)
    {
        var deviceIds = new[] { edge.Node1Id, edge.Node2Id }
            .Select(id => _map?.Nodes.FirstOrDefault(n => n.Id == id)?.DeviceId)
            .OfType<int>()
            .Distinct()
            .Where(id => !_portsByDevice.ContainsKey(id))
            .ToList();

        if (deviceIds.Count == 0)
        {
            return;
        }

        await FetchPortsAsync(deviceIds).ConfigureAwait(true);

        if (ReferenceEquals(_editor, editor))
        {
            editor.Ports = PortOptions(edge);
        }
    }

    // ------------------------------------------------------------------ status

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            _devices = result.Devices;
            _maintenanceIds = result.DeviceIdsUnderMaintenance;
            RebuildVisuals();
            _ = RefreshPortsAsync(force: false);
        });
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() =>
    {
        if (!_session.IsConnected)
        {
            _devices = Array.Empty<Device>();
            _ports.Clear();
            _portsByDevice.Clear();
            RebuildVisuals();
        }
    });

    /// <summary>Re-reads traffic for every device with a port-linked link on the current map - at most every <see cref="PortRefreshInterval"/> unless forced.</summary>
    private async Task RefreshPortsAsync(bool force)
    {
        if (_map is null || _isFetchingPorts || !_session.IsConnected)
        {
            return;
        }

        if (!force && DateTimeOffset.Now - _portsFetchedAt < PortRefreshInterval)
        {
            return;
        }

        var nodes = _map.Nodes.ToDictionary(n => n.Id);
        var deviceIds = _map.Edges
            .Where(e => e.PortId is not null)
            .SelectMany(e => new[] { nodes.GetValueOrDefault(e.Node1Id)?.DeviceId, nodes.GetValueOrDefault(e.Node2Id)?.DeviceId })
            .OfType<int>()
            .Distinct()
            .ToList();

        if (deviceIds.Count == 0)
        {
            return;
        }

        await FetchPortsAsync(deviceIds).ConfigureAwait(true);
        _portsFetchedAt = DateTimeOffset.Now;
        RebuildVisuals();
    }

    private async Task FetchPortsAsync(IReadOnlyList<int> deviceIds)
    {
        _isFetchingPorts = true;
        try
        {
            // A few at a time - one call per device, like the Ports tab.
            foreach (var batch in deviceIds.Chunk(4))
            {
                var results = await Task.WhenAll(batch.Select(FetchOneAsync)).ConfigureAwait(true);

                foreach (var (id, ports) in results)
                {
                    if (ports is null)
                    {
                        continue;
                    }

                    _portsByDevice[id] = ports;
                    foreach (var port in ports)
                    {
                        _ports[port.PortId] = port;
                    }
                }
            }
        }
        finally
        {
            _isFetchingPorts = false;
        }
    }

    private async Task<(int Id, IReadOnlyList<Port>? Ports)> FetchOneAsync(int deviceId)
    {
        try
        {
            return (deviceId, await _client.Ports.ListForDeviceAsync(deviceId).ConfigureAwait(false));
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load ports for device {DeviceId} on a custom map", deviceId);
            return (deviceId, null);
        }
    }

    /// <summary>
    /// Works out how every node and link should look now, LibreNMS's way:
    /// a node linked to a down device (or to a map with anything down)
    /// takes the "down" styling, a disabled one the "disabled" styling; a
    /// port-linked link colours each half by that direction's utilisation,
    /// or dark red if its device or port is down. While editing, nodes keep
    /// their own colours so their styling can be seen and set.
    /// </summary>
    private void RebuildVisuals()
    {
        if (_map is null)
        {
            Visuals = CustomMapVisuals.Empty;
            return;
        }

        var devices = _devices.ToDictionary(d => d.DeviceId);
        var nodeVisuals = new Dictionary<string, CustomMapNodeVisual>();

        foreach (var node in _map.Nodes)
        {
            var own = new CustomMapNodeVisual(node.BackgroundColour, node.BorderColour, node.TextColour);
            if (_isEditing)
            {
                nodeVisuals[node.Id] = own;
                continue;
            }

            var device = node.DeviceId is { } id ? devices.GetValueOrDefault(id) : null;
            nodeVisuals[node.Id] =
                device is { State: DeviceState.Disabled or DeviceState.Ignored } ? DisabledStyle :
                device is { State: DeviceState.Down } && !_maintenanceIds.Contains(device.DeviceId) ? DownStyle :
                node.LinkedMapId is { } linked && LinkedMapIsDown(linked, devices) ? DownStyle :
                own;
        }

        var edgeVisuals = new Dictionary<string, CustomMapEdgeVisual>();
        foreach (var edge in _map.Edges)
        {
            edgeVisuals[edge.Id] = EdgeVisual(edge, devices);
        }

        Visuals = new CustomMapVisuals(nodeVisuals, edgeVisuals);
    }

    private CustomMapEdgeVisual EdgeVisual(CustomMapEdge edge, IReadOnlyDictionary<int, Device> devices)
    {
        if (edge.PortId is not { } portId || !_ports.TryGetValue(portId, out var port))
        {
            var width = edge.FixedWidth ?? 1.5;
            var colour = edge.PortId is null ? PlainLinkColour : LinkUtilisation.UnknownColour;
            return new CustomMapEdgeVisual(colour, colour, width, width, edge.Label, string.Empty);
        }

        var legend = _map!.Legend.Colours;
        var speedWidth = edge.FixedWidth ?? LinkUtilisation.Width(port.IfSpeed);

        var deviceDown = devices.TryGetValue(port.DeviceId, out var device) && device.State == DeviceState.Down;
        if (deviceDown || !port.IsUp)
        {
            var down = legend?.GetValueOrDefault("-2") ?? LinkUtilisation.DownColour;
            return new CustomMapEdgeVisual(down, down, speedWidth, speedWidth, edge.Label, string.Empty);
        }

        // LibreNMS: "from" (node 1's half) is the port's outbound traffic,
        // "to" its inbound - swapped when the link is reversed.
        var outBps = port.IfOutOctetsRate * 8;
        var inBps = port.IfInOctetsRate * 8;
        var (fromBps, toBps) = edge.Reverse ? (inBps, outBps) : (outBps, inBps);

        var fromPct = LinkUtilisation.Percent(fromBps, port.IfSpeed);
        var toPct = LinkUtilisation.Percent(toBps, port.IfSpeed);

        return new CustomMapEdgeVisual(
            LinkUtilisation.Colour(fromPct, legend),
            LinkUtilisation.Colour(toPct, legend),
            speedWidth,
            speedWidth,
            HalfLabel(edge, fromPct, fromBps, edge.Label),
            HalfLabel(edge, toPct, toBps, string.Empty));
    }

    private static string HalfLabel(CustomMapEdge edge, double percent, double? bps, string extra)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(extra))
        {
            parts.Add(extra);
        }

        if (edge.ShowPercent && percent >= 0)
        {
            parts.Add($"{percent:0}%");
        }

        if (edge.ShowBps && bps is not null)
        {
            parts.Add(LinkUtilisation.Rate(bps));
        }

        return string.Join("  ", parts);
    }

    /// <summary>LibreNMS's linkedMapIsDown: any device on the linked map is down.</summary>
    private bool LinkedMapIsDown(string mapId, IReadOnlyDictionary<int, Device> devices) =>
        _store.Load(mapId) is { } linked
        && linked.Nodes.Any(n => n.DeviceId is { } id && devices.TryGetValue(id, out var d) && d.State == DeviceState.Down);

    private void ShowViewSelection(string nodeId)
    {
        var node = _map?.Nodes.FirstOrDefault(n => n.Id == nodeId);
        var device = node?.DeviceId is { } id ? _devices.FirstOrDefault(d => d.DeviceId == id) : null;

        SelectedNodeId = nodeId;
        ViewSelectionName = device is not null
            ? _settings.Current.DeviceNameStyle.Resolve(device, device.Hostname)
            : node?.Label;
        ViewSelectionState = device is null
            ? node?.LinkedMapId is { } linked ? $"Links to {Maps.FirstOrDefault(m => m.Id == linked)?.Name ?? "a map"}" : "No device"
            : _maintenanceIds.Contains(device.DeviceId) ? "In maintenance"
            : device.State switch { DeviceState.Up => "Up", DeviceState.Down => "Down", DeviceState.Disabled => "Disabled", DeviceState.Ignored => "Ignored", _ => string.Empty };

        OnPropertyChanged(nameof(ViewSelectionName));
        OnPropertyChanged(nameof(ViewSelectionState));
        OnPropertyChanged(nameof(HasViewSelection));
    }

    private void ClearViewSelection()
    {
        if (!_isEditing)
        {
            SelectedNodeId = null;
        }

        ViewSelectionName = null;
        ViewSelectionState = null;
        OnPropertyChanged(nameof(ViewSelectionName));
        OnPropertyChanged(nameof(ViewSelectionState));
        OnPropertyChanged(nameof(HasViewSelection));
    }

    private void OpenSelected()
    {
        if (_selectedNodeId is { } id)
        {
            OnNodeActivated(id);
        }
    }

    private void RaiseCommands()
    {
        ExportMapCommand.RaiseCanExecuteChanged();
        DuplicateMapCommand.RaiseCanExecuteChanged();
        DeleteMapCommand.RaiseCanExecuteChanged();
        EditMapCommand.RaiseCanExecuteChanged();
        SaveMapCommand.RaiseCanExecuteChanged();
        CancelEditCommand.RaiseCanExecuteChanged();
        DeleteSelectionCommand.RaiseCanExecuteChanged();
        RecentreLinksCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(HasViewSelection));
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _deviceMonitor.Polled -= OnDevicesPolled;
        _session.StateChanged -= OnSessionStateChanged;
    }
}

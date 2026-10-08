using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The "Graph" dashboard widget (issue #12): pick any device and any one of
/// its graphs (device-wide or health-category, same merged listing as the
/// Device Details Graphs section) plus a time range, and it renders that
/// graph right on the Dashboard. There is no fleet-wide graph endpoint
/// (confirmed live - LibreNMS's graph routes are all per-device), so rather
/// than a single "fleet health" widget, each instance of this widget is its
/// own configurable window onto one device's one graph; a Dashboard with
/// several of these side by side is how a fleet-wide view gets built.
/// Reloads whenever <see cref="DashboardViewModel"/> asks (on showing the
/// Dashboard tab and on manual refresh) - never on a background timer, so a
/// widget sitting unseen on the Dashboard while another tab is open costs no
/// API traffic.
/// </summary>
public sealed class GraphWidgetViewModel : DashboardWidgetViewModel, IDisposable
{
    private readonly DeviceMonitor _deviceMonitor;
    private readonly ILibreNmsClient _client;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Action<int, string> _openGraph;
    private readonly Action<int, string, string>? _openPortGraph;
    private readonly List<GraphType> _deviceGraphs = new();
    private GraphSource? _selectedSource;
    private string? _wantedPortIfName;

    private IReadOnlyList<Device> _fleet = Array.Empty<Device>();
    private int? _selectedDeviceId;
    private string _selectedDeviceName = string.Empty;
    private GraphType? _selectedGraph;
    private string? _wantedGraphName;
    private bool _isDevicePickerOpen;
    private string _devicePickerSearchText = string.Empty;
    private bool _isLoading;
    private string? _errorMessage;
    private string? _svg;
    private int _loadVersion;

    /// <param name="openGraph">Opens this device's Device Details on the given graph - a callback (like the other device-shaped widgets' own) rather than an IWindowService dependency.</param>
    /// <param name="openPortGraph">Opens a port's graph full size in Device Details' Graphs (#285): device, ifName, graph type.</param>
    public GraphWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, DeviceMonitor deviceMonitor, ILibreNmsClient client, ILogger logger, Action<int, string> openGraph, Action<int, string, string>? openPortGraph = null)
        : base(layout, model)
    {
        _deviceMonitor = deviceMonitor;
        _client = client;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _openGraph = openGraph;
        _openPortGraph = openPortGraph;

        _selectedDeviceId = model.GraphDeviceId;
        _wantedGraphName = model.GraphName;
        _wantedPortIfName = model.GraphPortIfName;
        TimeRange = new GraphTimeRangeViewModel(model.GraphTimeRangePreset, model.GraphCustomFrom, model.GraphCustomTo);
        TimeRange.Changed += OnTimeRangeChanged;

        AvailableGraphs = new ObservableCollection<GraphType>();
        DevicePickerResults = new BatchObservableCollection<DevicePickerItem>();

        ToggleDevicePickerCommand = new RelayCommand(() =>
        {
            IsDevicePickerOpen = !IsDevicePickerOpen;
            if (IsDevicePickerOpen)
            {
                RefreshDevicePicker();
            }
        });

        SelectDeviceCommand = new RelayCommand(parameter =>
        {
            if (parameter is DevicePickerItem item)
            {
                SelectDevice(item.DeviceId, item.DisplayName);
            }
        });

        // No CanExecute: the device/graph arrive asynchronously and a
        // RelayCommand never re-queries on its own, so a gate here would
        // stay stuck disabled. The view only shows the clickable graph once
        // one has rendered anyway; this just guards the (unlikely) gap.
        OpenGraphCommand = new RelayCommand(() =>
        {
            if (_selectedDeviceId is not { } deviceId || _selectedGraph is not { } graph)
            {
                return;
            }

            if (_selectedSource?.IfName is { } ifName && _openPortGraph is not null)
            {
                _openPortGraph(deviceId, ifName, graph.Name);
            }
            else
            {
                _openGraph(deviceId, graph.Name);
            }
        });

        // Device data has to come from somewhere - DeviceMonitor is lazily
        // started (see DeviceStatusWidgetViewModel's own reasoning), and this
        // widget may be the only thing on the Dashboard asking for it.
        _deviceMonitor.Polled += OnDevicesPolled;
        _deviceMonitor.Start();
        _deviceMonitor.RequestRefresh();

        if (_selectedDeviceId is { } deviceId)
        {
            _ = LoadGraphTypesAsync(deviceId, model.GraphName, model.GraphPortIfName);
        }
    }

    public ObservableCollection<GraphType> AvailableGraphs { get; }

    /// <summary>The device's own graphs, then each of its ports (#285) - as Device Details' Graphs offers them.</summary>
    public ObservableCollection<GraphSource> Sources { get; } = new();

    public bool HasSources => Sources.Count > 1;

    /// <summary>Device graphs or a port: switching swaps the graph list, keeping the same graph where the new list has it.</summary>
    public GraphSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedSource))
            {
                return;
            }

            var keep = _selectedGraph?.Name;
            _selectedSource = value;
            OnPropertyChanged();
            FillGraphs(value);
            SelectedGraph = AvailableGraphs.FirstOrDefault(g => g.Name == keep) ?? AvailableGraphs.FirstOrDefault();
        }
    }

    /// <summary>"Gi0/1 · uplink" while a port's graph is shown, for the widget's subtitle.</summary>
    public string? PortLabel => _selectedSource?.IsPort == true ? _selectedSource.Name : null;

    private void FillGraphs(GraphSource source)
    {
        _selectedGraph = null;
        AvailableGraphs.Clear();
        foreach (var type in source.IsPort ? PortGraphsPanelViewModel.Types : _deviceGraphs)
        {
            AvailableGraphs.Add(type);
        }

        OnPropertyChanged(nameof(PortLabel));
    }

    public GraphTimeRangeViewModel TimeRange { get; }

    public RelayCommand ToggleDevicePickerCommand { get; }

    public RelayCommand SelectDeviceCommand { get; }

    /// <summary>Clicking the rendered graph (issue #161) opens the device's Device Details on this same graph, full size.</summary>
    public RelayCommand OpenGraphCommand { get; }

    public BatchObservableCollection<DevicePickerItem> DevicePickerResults { get; }

    public bool HasDevicePickerResults => DevicePickerResults.Count > 0;

    /// <summary>True while the "choose a device" picker is showing in place of the graph-type list.</summary>
    public bool IsDevicePickerOpen
    {
        get => _isDevicePickerOpen;
        set => SetProperty(ref _isDevicePickerOpen, value);
    }

    public string DevicePickerSearchText
    {
        get => _devicePickerSearchText;
        set
        {
            if (SetProperty(ref _devicePickerSearchText, value))
            {
                RefreshDevicePicker();
            }
        }
    }

    public string SelectedDeviceName
    {
        get => _selectedDeviceName;
        private set => SetProperty(ref _selectedDeviceName, value);
    }

    public bool HasSelectedDevice => _selectedDeviceId is not null;

    public GraphType? SelectedGraph
    {
        get => _selectedGraph;
        set
        {
            if (SetProperty(ref _selectedGraph, value))
            {
                _wantedGraphName = value?.Name ?? _wantedGraphName;
                OnPropertyChanged(nameof(IsConfigured));
                Layout.SetGraph(Id, _selectedDeviceId, value?.Name, _selectedSource?.IfName);
                _ = LoadGraphAsync();
            }
        }
    }

    /// <summary>True once both a device and a graph have been chosen - drives the "pick a graph" placeholder.</summary>
    public bool IsConfigured => _selectedDeviceId is not null && _selectedGraph is not null;

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

    public override void SyncFrom(DashboardWidget model)
    {
        base.SyncFrom(model);

        if (_selectedDeviceId != model.GraphDeviceId)
        {
            _selectedDeviceId = model.GraphDeviceId;
            SelectedDeviceName = ResolveDeviceName(_selectedDeviceId);
            OnPropertyChanged(nameof(HasSelectedDevice));
        }
    }

    protected override void OnEditingClosed() => IsDevicePickerOpen = false;

    /// <summary>Re-fetches the current graph, if one is configured - called by <see cref="DashboardViewModel"/> when the Dashboard tab is shown or refreshed. A no-op otherwise, rather than a background timer, so an unconfigured or unseen widget never costs API traffic.</summary>
    public void Reload()
    {
        if (IsConfigured)
        {
            _ = LoadGraphAsync();
        }
        else if (_selectedDeviceId is { } deviceId && AvailableGraphs.Count == 0)
        {
            // Its graph list never loaded (the server wasn't answering at the
            // time) - try again, and pick up the graph it was set to.
            _ = LoadGraphTypesAsync(deviceId, _wantedGraphName, _wantedPortIfName);
        }
    }

    private void SelectDevice(int deviceId, string displayName)
    {
        _selectedDeviceId = deviceId;
        SelectedDeviceName = displayName;
        _selectedGraph = null;
        _selectedSource = null;
        _wantedPortIfName = null;
        AvailableGraphs.Clear();
        Sources.Clear();
        Svg = null;
        ErrorMessage = null;
        IsDevicePickerOpen = false;

        OnPropertyChanged(nameof(HasSelectedDevice));
        OnPropertyChanged(nameof(SelectedGraph));
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(PortLabel));
        OnPropertyChanged(nameof(IsConfigured));

        Layout.SetGraph(Id, deviceId, null);
        _ = LoadGraphTypesAsync(deviceId, selectGraphName: null, selectPortIfName: null);
    }

    /// <param name="selectPortIfName">Shows this port's graphs (#285) rather than the device's, if the device still has the port.</param>
    private async Task LoadGraphTypesAsync(int deviceId, string? selectGraphName, string? selectPortIfName)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var deviceWideTask = _client.Graphs.ListAsync(deviceId);
            var healthTask = _client.Graphs.ListHealthAsync(deviceId);
            var wirelessTask = GraphsSectionViewModel.ListWirelessGraphsAsync(_client, deviceId, _logger);
            var portsTask = ListPortsAsync(deviceId);
            await Task.WhenAll(deviceWideTask, healthTask, wirelessTask, portsTask).ConfigureAwait(true);

            _deviceGraphs.Clear();
            _deviceGraphs.AddRange(deviceWideTask.Result.Concat(healthTask.Result).Concat(wirelessTask.Result).OrderBy(t => t.Description, StringComparer.OrdinalIgnoreCase));

            Sources.Clear();
            var deviceSource = new GraphSource("Device graphs", null);
            Sources.Add(deviceSource);
            foreach (var port in portsTask.Result)
            {
                Sources.Add(new GraphSource(GraphsSectionViewModel.PortLabel(port), port));
            }

            _selectedSource = (selectPortIfName is not null ? Sources.FirstOrDefault(s => s.IfName == selectPortIfName) : null) ?? deviceSource;
            OnPropertyChanged(nameof(SelectedSource));
            OnPropertyChanged(nameof(HasSources));
            FillGraphs(_selectedSource);

            var toSelect = (selectGraphName is not null ? AvailableGraphs.FirstOrDefault(g => g.Name == selectGraphName) : null)
                ?? AvailableGraphs.FirstOrDefault();

            if (toSelect is not null)
            {
                SelectedGraph = toSelect;
            }
            else
            {
                IsLoading = false;
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load graph types for device {DeviceId} (Graph widget {WidgetId})", deviceId, Id);
            ErrorMessage = ex.ToUserMessage();
            IsLoading = false;
        }
    }

    private async Task LoadGraphAsync()
    {
        if (_selectedDeviceId is not { } deviceId || SelectedGraph is not { } graph)
        {
            return;
        }

        var version = ++_loadVersion;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // A generous fetch size - the widget's own cell varies with
            // however the user resized it, and being a vector image it
            // scales cleanly either way (see the same reasoning behind the
            // Overview ping-graph thumbnail, issue #11), but starting from
            // too small a fetch leaves LibreNMS's own legend/axis text too
            // sparse to be legible once stretched up.
            var rawSvg = _selectedSource?.IfName is { } ifName
                ? await _client.Graphs.GetPortSvgAsync(deviceId, ifName, graph.Name, TimeRange.ToTimeRange(), width: 500, height: 220, legend: false).ConfigureAwait(true)
                : await _client.Graphs.GetSvgAsync(deviceId, graph.Name, TimeRange.ToTimeRange(), width: 500, height: 220, legend: false).ConfigureAwait(true);

            if (version != _loadVersion)
            {
                return;
            }

            Svg = GraphSvgTheming.ApplyCurrentTheme(rawSvg);
        }
        catch (LibreNmsApiException ex)
        {
            if (version != _loadVersion)
            {
                return;
            }

            _logger.LogWarning(ex, "Could not load graph {GraphName} for device {DeviceId} (Graph widget {WidgetId})", graph.Name, deviceId, Id);
            ErrorMessage = ex.ToUserMessage();
            Svg = null;
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>The device's ports with a name, in interface order - empty, rather than failing the widget, when they can't be listed.</summary>
    private async Task<IReadOnlyList<Port>> ListPortsAsync(int deviceId)
    {
        try
        {
            return (await _client.Ports.ListForDeviceAsync(deviceId).ConfigureAwait(true))
                .Where(p => !p.Deleted && !string.IsNullOrEmpty(p.IfName))
                .OrderBy(p => p.IfIndex ?? int.MaxValue)
                .ThenBy(p => p.IfName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogDebug(ex, "Could not list the ports of device {DeviceId} (Graph widget {WidgetId})", deviceId, Id);
            return Array.Empty<Port>();
        }
    }

    private void OnTimeRangeChanged(object? sender, EventArgs e)
    {
        Layout.SetGraphTimeRange(Id, TimeRange.Preset, TimeRange.IsCustom ? TimeRange.CustomFrom : null, TimeRange.IsCustom ? TimeRange.CustomTo : null);
        _ = LoadGraphAsync();
    }

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            _fleet = result.Devices;

            if (_selectedDeviceId is { } deviceId)
            {
                SelectedDeviceName = ResolveDeviceName(deviceId);
            }

            if (IsDevicePickerOpen)
            {
                RefreshDevicePicker();
            }
        });
    }

    private string ResolveDeviceName(int? deviceId)
    {
        if (deviceId is null)
        {
            return string.Empty;
        }

        var match = _fleet.FirstOrDefault(d => d.DeviceId == deviceId);
        return match?.BestName ?? $"device {deviceId}";
    }

    private void RefreshDevicePicker()
    {
        var term = DevicePickerSearchText?.Trim();

        var candidates = _fleet
            .Select(d => new DevicePickerItem(d.DeviceId, d.BestName))
            .Where(d => string.IsNullOrEmpty(term) || d.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(50);

        DevicePickerResults.ReplaceAll(candidates);

        OnPropertyChanged(nameof(HasDevicePickerResults));
    }

    public void Dispose()
    {
        _deviceMonitor.Polled -= OnDevicesPolled;
        TimeRange.Changed -= OnTimeRangeChanged;
    }
}

/// <summary>One row in a Graph widget's "choose a device" picker.</summary>
public sealed class DevicePickerItem
{
    public DevicePickerItem(int deviceId, string displayName)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
    }

    public int DeviceId { get; }

    public string DisplayName { get; }
}

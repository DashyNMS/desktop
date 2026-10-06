using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Graphs;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the Device Details "Graphs" section (issues #14/#13/#17/#20) -
/// device-wide graphs only, fetched as LibreNMS's own rendered SVG and
/// recoloured for the current theme (see Infrastructure.GraphSvgTheming),
/// rendered via SharpVectors' SvgViewbox (bound directly to the SVG markup
/// via its SvgSource property - no temp file needed). Ports and per-sensor
/// graphs are out of scope - neither is reachable via the API token this
/// app authenticates with (confirmed live, see issue #8's own
/// investigation and this issue's own follow-up testing).
/// </summary>
public sealed class GraphsSectionViewModel : ObservableObject
{
    private readonly int _deviceId;
    private readonly ILibreNmsClient _client;
    private readonly ILogger _logger;

    /// <summary>LibreNMS's SVG as sent, per graph, range and legend, kept for exactly this view model's lifetime - the Device Details window's (issue #20) - so turning series on and off needs no new request. Re-opening the same device later is a fresh, deliberate re-fetch.</summary>
    private readonly Dictionary<(string GraphName, GraphTimeRange Range, bool Legend), string> _cache = new();
    private readonly ISettingsStore _settings;

    private bool _hasLoadedOnce;
    private Task? _loadTypesTask;
    private GraphType? _selectedGraph;
    private bool _isLoading;
    private string? _errorMessage;
    private string? _currentSvg;

    public GraphsSectionViewModel(int deviceId, ILibreNmsClient client, ISettingsStore settings, ILogger logger)
    {
        _deviceId = deviceId;
        _settings = settings;
        _client = client;
        _logger = logger;

        AvailableGraphs = new ObservableCollection<GraphType>();
        TimeRange = new GraphTimeRangeViewModel();
        TimeRange.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(GraphTitle));
            _ = LoadSelectedGraphAsync();
        };
    }

    public ObservableCollection<GraphType> AvailableGraphs { get; }

    public GraphTimeRangeViewModel TimeRange { get; }

    public GraphType? SelectedGraph
    {
        get => _selectedGraph;
        set
        {
            if (SetProperty(ref _selectedGraph, value))
            {
                OnPropertyChanged(nameof(GraphTitle));
                OnPropertyChanged(nameof(LegendTitle));
                _ = LoadSelectedGraphAsync();
            }
        }
    }

    /// <summary>The graph card's heading: "TEMPERATURE · LAST 24 HOURS".</summary>
    public string GraphTitle
    {
        get
        {
            var range = TimeRange.Preset switch
            {
                GraphTimeRangePreset.Hour => "LAST HOUR",
                GraphTimeRangePreset.Day => "LAST 24 HOURS",
                GraphTimeRangePreset.Week => "LAST 7 DAYS",
                GraphTimeRangePreset.Month => "LAST 30 DAYS",
                GraphTimeRangePreset.Year => "LAST YEAR",
                _ => "CUSTOM RANGE",
            };
            return _selectedGraph is { } graph ? $"{graph.Description.ToUpperInvariant()} · {range}" : range;
        }
    }

    /// <summary>The series card's heading.</summary>
    public string LegendTitle => _selectedGraph?.Name switch
    {
        GraphLegend.ProcessorGraph => "PROCESSORS",
        { } name when GraphLegend.SensorClassOf(name) is not null => "SENSORS",
        _ => "SERIES",
    };

    /// <summary>The recoloured SVG markup, bound directly to SvgViewbox.SvgSource - null while loading/on error/before anything is selected.</summary>
    public string? CurrentSvg
    {
        get => _currentSvg;
        private set => SetProperty(ref _currentSvg, value);
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

    /// <summary>
    /// Loads the available graph types on first visit to this section only
    /// - same lazy-on-first-visit shape as DeviceDetailViewModel's own
    /// poller-group list, rather than paying for this on every device
    /// window open regardless of whether Graphs is ever looked at.
    /// </summary>
    public void EnsureLoaded()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _hasLoadedOnce = true;
        _loadTypesTask = LoadGraphTypesAsync(selectGraphName: null);
    }

    /// <summary>
    /// Switches to the graph named <paramref name="graphName"/> - used by a
    /// "view graph" quick link elsewhere in Device Details (e.g. Resources'
    /// Processor/Memory/Storage cards). If the type list has never been
    /// loaded, this drives that load itself and selects the match directly
    /// - rather than defaulting to the first graph and then immediately
    /// correcting to this one, which would fetch and briefly show the wrong
    /// graph first. If a load from <see cref="EnsureLoaded"/> is already in
    /// flight, awaits that instead of racing it.
    /// </summary>
    public async Task SelectGraphByNameAsync(string graphName)
    {
        if (!_hasLoadedOnce)
        {
            _hasLoadedOnce = true;
            _loadTypesTask = LoadGraphTypesAsync(graphName);
            await _loadTypesTask.ConfigureAwait(true);
            return;
        }

        if (_loadTypesTask is { } task)
        {
            await task.ConfigureAwait(true);
        }

        if (AvailableGraphs.FirstOrDefault(g => g.Name == graphName) is { } match)
        {
            SelectedGraph = match;
        }
    }

    /// <param name="selectGraphName">
    /// Selects this specific graph once loaded, if the device has it;
    /// falls back to the first graph (the plain first-visit default) when
    /// null or not found.
    /// </param>
    private async Task LoadGraphTypesAsync(string? selectGraphName)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Device-wide graphs (poller time, ping, uptime, netstat, ...)
            // and health-category graphs (processor, mempool, storage,
            // temperature, ...) are two distinct listings - confirmed live
            // neither ever includes the other - though both render through
            // the same /devices/{id}/{graphName} mechanism, so they can
            // simply be combined into one picker.
            var deviceWideTask = _client.Graphs.ListAsync(_deviceId);
            var healthTask = _client.Graphs.ListHealthAsync(_deviceId);
            var wirelessTask = ListWirelessGraphsAsync(_client, _deviceId, _logger);
            await Task.WhenAll(deviceWideTask, healthTask, wirelessTask).ConfigureAwait(true);

            AvailableGraphs.Clear();
            foreach (var type in deviceWideTask.Result.Concat(healthTask.Result).Concat(wirelessTask.Result).OrderBy(t => t.Description, StringComparer.OrdinalIgnoreCase))
            {
                AvailableGraphs.Add(type);
            }

            // Selecting a graph triggers its own load (see SelectedGraph's
            // setter), which will clear IsLoading itself - only clear it
            // here when there was nothing to select at all.
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
            _logger.LogWarning(ex, "Could not load graph types for device {DeviceId}", _deviceId);
            ErrorMessage = ex.ToUserMessage();
            IsLoading = false;
        }
    }

    /// <summary>
    /// The device's wireless graphs (#55) - a third listing besides the
    /// device-wide and health ones. Best effort: most devices have none, and
    /// a failure here shouldn't take the other graphs down with it.
    /// </summary>
    internal static async Task<IReadOnlyList<GraphType>> ListWirelessGraphsAsync(ILibreNmsClient client, int deviceId, ILogger logger)
    {
        try
        {
            return await client.Graphs.ListWirelessAsync(deviceId).ConfigureAwait(true);
        }
        catch (LibreNmsApiException ex)
        {
            logger.LogDebug(ex, "Could not list wireless graphs for device {DeviceId}", deviceId);
            return Array.Empty<GraphType>();
        }
    }

    private async Task LoadSelectedGraphAsync()
    {
        if (SelectedGraph is not { } graph)
        {
            return;
        }

        var range = TimeRange.ToTimeRange();
        var version = ++_loadVersion;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // LibreNMS draws its legend; its entries (a colour square each)
            // say how many series there are and in what colours. Our own
            // legend replaces it where they can be named - from the API
            // (sensors, processors, traffic, with values), from LibreNMS's own
            // graph definitions (GraphSeriesNames), or the graph's own name
            // for a lone series - and LibreNMS's stays otherwise.
            var raw = await RawAsync(graph.Name, range, legend: true).ConfigureAwait(true);
            var legendColours = GraphSvgTheming.Restyle(raw).LegendColours;
            var entries = await NameSeriesAsync(graph, legendColours).ConfigureAwait(true);
            if (entries is null)
            {
                _logger.LogDebug("Can't name the {Count} series of {GraphName} for device {DeviceId} - showing LibreNMS's legend", legendColours.Count, graph.Name, _deviceId);
            }

            if (version != _loadVersion)
            {
                return;
            }

            _raw = raw;
            _entries = entries;
            _hidden = new HashSet<string>(
                entries is null ? Enumerable.Empty<string>() : HiddenSetting(graph.Name).Where(n => entries.Any(e => e.Name == n)),
                StringComparer.Ordinal);
            await RenderAsync(version).ConfigureAwait(true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load graph {GraphName} for device {DeviceId}", graph.Name, _deviceId);
            ErrorMessage = ex.ToUserMessage();
            CurrentSvg = null;
            Legend.Clear();
            RaiseLegendChanged();
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    // --------------------------------------------------------------- legend

    private const int GraphWidth = 1000;
    // About the graph card's shape beside the series card, so the graph fills it.
    private const int GraphHeight = 520;

    private int _loadVersion;
    private string? _raw;
    private IReadOnlyList<GraphLegendEntry>? _entries;
    private HashSet<string> _hidden = new(StringComparer.Ordinal);
    private IReadOnlyList<Sensor>? _sensors;

    /// <summary>The app's own legend (see GraphLegend) - empty when LibreNMS's legend is in the graph instead.</summary>
    public ObservableCollection<GraphLegendItemViewModel> Legend { get; } = new();

    public bool HasLegend => Legend.Count > 0;

    /// <summary>Counted from the legend, not the saved names - a remembered sensor that has since gone hides nothing.</summary>
    public bool HasHiddenSeries => Legend.Any(i => !i.IsShown);

    /// <summary>"4 of 6 shown".</summary>
    public string ShownText => $"{Legend.Count(i => i.IsShown)} of {Legend.Count} shown";

    public RelayCommand ShowAllCommand => _showAll ??= new RelayCommand(() => SetHidden(Array.Empty<string>()));

    private RelayCommand? _showAll;

    /// <summary>Hides or shows one series.</summary>
    internal void Toggle(string name)
        => SetHidden(_hidden.Contains(name) ? _hidden.Where(n => n != name) : _hidden.Append(name));

    /// <summary>Shows only this series.</summary>
    internal void ShowOnly(string name)
        => SetHidden(Legend.Select(i => i.Name).Where(n => n != name));

    private void SetHidden(IEnumerable<string> hidden)
    {
        var next = new HashSet<string>(hidden, StringComparer.Ordinal);

        // Turning the last one off too would leave an empty graph - not something to ask for.
        if (_entries is null || _entries.All(e => next.Contains(e.Name)))
        {
            return;
        }

        _hidden = next;
        if (SelectedGraph is { } graph)
        {
            var key = HiddenKey(graph.Name);
            if (_hidden.Count == 0)
            {
                _settings.Current.GraphHiddenSeries.Remove(key);
            }
            else
            {
                _settings.Current.GraphHiddenSeries[key] = _hidden.ToList();
            }

            _settings.SaveQuietly();
        }

        _ = RenderAsync(_loadVersion);
    }

    /// <summary>The graph as it should look now: the series shown, and the scale fitted to a lone sensor.</summary>
    private async Task RenderAsync(int version)
    {
        if (_raw is not { } raw)
        {
            return;
        }

        if (_entries is not { } entries)
        {
            Legend.Clear();
            CurrentSvg = GraphSvgTheming.Restyle(raw).Svg;
            RaiseLegendChanged();
            return;
        }

        var hiddenIndexes = entries.Select((e, i) => (e, i)).Where(x => _hidden.Contains(x.e.Name)).Select(x => x.i).ToHashSet();
        var styled = GraphSvgTheming.Restyle(raw, hiddenIndexes, cropLegend: true);

        SyncLegend(entries, styled.SeriesColours);

        var shown = entries.Where(e => !_hidden.Contains(e.Name)).ToList();
        if (shown is [{ SensorId: { } sensorId } only] && entries.Count > 1 && SelectedGraph is { } graph)
        {
            // One sensor left: its own graph, so the scale fits it rather than every sensor.
            try
            {
                var colour = styled.SeriesColours[entries.ToList().IndexOf(only)];
                var single = await SensorRawAsync(graph.Name, sensorId, TimeRange.ToTimeRange()).ConfigureAwait(true);
                if (version != _loadVersion || !_hidden.SetEquals(entries.Where(e => e != only).Select(e => e.Name)))
                {
                    return;
                }

                CurrentSvg = GraphSvgTheming.Restyle(single, mainColour: colour).Svg;
                return;
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogDebug(ex, "Could not load sensor {SensorId}'s own graph - keeping the shared scale", sensorId);
            }
        }

        CurrentSvg = styled.Svg;
    }

    private void SyncLegend(IReadOnlyList<GraphLegendEntry> entries, IReadOnlyList<string> colours)
    {
        if (Legend.Count != entries.Count || Legend.Select(i => i.Name).Zip(entries, (a, b) => a == b.Name).Any(same => !same))
        {
            Legend.Clear();
            for (var i = 0; i < entries.Count; i++)
            {
                Legend.Add(new GraphLegendItemViewModel(entries[i], colours[i], this));
            }
        }

        foreach (var item in Legend)
        {
            item.IsShown = !_hidden.Contains(item.Name);
        }

        RaiseLegendChanged();
    }

    private void RaiseLegendChanged()
    {
        OnPropertyChanged(nameof(HasLegend));
        OnPropertyChanged(nameof(HasHiddenSeries));
        OnPropertyChanged(nameof(ShownText));
    }

    /// <summary>The series names for this graph, in LibreNMS's drawing order - or null when it isn't a graph we can name the series of.</summary>
    /// <summary>
    /// Names for each of the graph's series (one per LibreNMS legend entry,
    /// in its colours) - the API's where it has them, then LibreNMS's own
    /// graph definitions, then the graph's own name for a lone series. Null
    /// when none of them fits, so LibreNMS's legend stays.
    /// </summary>
    private async Task<IReadOnlyList<GraphLegendEntry>?> NameSeriesAsync(GraphType graph, IReadOnlyList<string> legendColours)
    {
        if (legendColours.Count == 0)
        {
            return null;
        }

        if (await LegendEntriesAsync(graph.Name).ConfigureAwait(true) is { } fromApi && fromApi.Count == legendColours.Count)
        {
            return fromApi;
        }

        if (GraphSeriesNames.Resolve(graph.Name, legendColours) is { } names)
        {
            return names.Select(n => new GraphLegendEntry(n, null, null)).ToList();
        }

        return legendColours.Count == 1 ? [new GraphLegendEntry(graph.Description ?? graph.Name, null, null)] : null;
    }

    private async Task<IReadOnlyList<GraphLegendEntry>?> LegendEntriesAsync(string graphName)
    {
        try
        {
            if (graphName == GraphLegend.TrafficGraph)
            {
                return GraphLegend.ForTraffic(await _client.Ports.ListForDeviceAsync(_deviceId).ConfigureAwait(true));
            }

            if (graphName == GraphLegend.ProcessorGraph)
            {
                return GraphLegend.ForProcessors(await _client.Health.ListProcessorsAsync(_deviceId).ConfigureAwait(true));
            }

            if (GraphLegend.SensorClassOf(graphName) is { } sensorClass)
            {
                _sensors ??= (await _client.Sensors.ListAsync().ConfigureAwait(true)).Where(s => s.DeviceId == _deviceId).ToList();
                var entries = GraphLegend.ForSensors(_sensors, sensorClass);
                return entries.Count > 0 ? entries : null;
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogDebug(ex, "Could not name the series of {GraphName} - showing LibreNMS's legend", graphName);
        }

        return null;
    }

    private async Task<string> RawAsync(string graphName, GraphTimeRange range, bool legend)
    {
        var key = (graphName, range, legend);
        if (!_cache.TryGetValue(key, out var raw))
        {
            raw = await _client.Graphs.GetSvgAsync(_deviceId, graphName, range, GraphWidth, GraphHeight, legend: legend).ConfigureAwait(true);
            _cache[key] = raw;
        }

        return raw;
    }

    private async Task<string> SensorRawAsync(string graphName, int sensorId, GraphTimeRange range)
    {
        var key = (graphName + "/" + sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture), range, false);
        if (!_cache.TryGetValue(key, out var raw))
        {
            raw = await _client.Graphs.GetSensorSvgAsync(_deviceId, graphName, sensorId, range, GraphWidth, GraphHeight, legend: false).ConfigureAwait(true);
            _cache[key] = raw;
        }

        return raw;
    }

    private string HiddenKey(string graphName) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_deviceId}:{graphName}");

    private IReadOnlyList<string> HiddenSetting(string graphName)
        => _settings.Current.GraphHiddenSeries.TryGetValue(HiddenKey(graphName), out var hidden) ? hidden : Array.Empty<string>();
}

/// <summary>One series in the Graphs section's legend: click to hide or show it, shift-click for only it.</summary>
public sealed class GraphLegendItemViewModel : ObservableObject
{
    private readonly GraphsSectionViewModel _owner;
    private bool _isShown = true;

    public GraphLegendItemViewModel(GraphLegendEntry entry, string colour, GraphsSectionViewModel owner)
    {
        _owner = owner;
        Name = entry.Name;
        Detail = entry.Detail;
        Value = entry.Value;
        Colour = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colour);
        ColourBrush = new System.Windows.Media.SolidColorBrush(Colour);
        ColourBrush.Freeze();
        ToggleCommand = new RelayCommand(() => _owner.Toggle(Name));
        OnlyCommand = new RelayCommand(() => _owner.ShowOnly(Name));
    }

    public string Name { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string? Value { get; }

    public System.Windows.Media.Color Colour { get; }

    public System.Windows.Media.SolidColorBrush ColourBrush { get; }

    public bool IsShown
    {
        get => _isShown;
        set => SetProperty(ref _isShown, value);
    }

    public RelayCommand ToggleCommand { get; }

    public RelayCommand OnlyCommand { get; }
}

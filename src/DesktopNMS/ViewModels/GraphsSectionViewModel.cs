using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
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

    /// <summary>Already-fetched-and-recoloured SVG per graph+range, kept for exactly this view model's lifetime - the Device Details window's lifetime (issue #20). Re-opening the same device later is a fresh, deliberate re-fetch; nothing here is persisted.</summary>
    private readonly Dictionary<(string GraphName, GraphTimeRange Range), string> _cache = new();

    private bool _hasLoadedOnce;
    private Task? _loadTypesTask;
    private GraphType? _selectedGraph;
    private bool _isLoading;
    private string? _errorMessage;
    private string? _currentSvg;

    public GraphsSectionViewModel(int deviceId, ILibreNmsClient client, ILogger logger)
    {
        _deviceId = deviceId;
        _client = client;
        _logger = logger;

        AvailableGraphs = new ObservableCollection<GraphType>();
        TimeRange = new GraphTimeRangeViewModel();
        TimeRange.Changed += (_, _) => _ = LoadSelectedGraphAsync();
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
                _ = LoadSelectedGraphAsync();
            }
        }
    }

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
            await Task.WhenAll(deviceWideTask, healthTask).ConfigureAwait(true);

            AvailableGraphs.Clear();
            foreach (var type in deviceWideTask.Result.Concat(healthTask.Result).OrderBy(t => t.Description, StringComparer.OrdinalIgnoreCase))
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

    private async Task LoadSelectedGraphAsync()
    {
        if (SelectedGraph is not { } graph)
        {
            return;
        }

        var range = TimeRange.ToTimeRange();
        var cacheKey = (graph.Name, range);

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            CurrentSvg = cached;
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var rawSvg = await _client.Graphs.GetSvgAsync(_deviceId, graph.Name, range, width: 1000, height: 350).ConfigureAwait(true);
            var themedSvg = GraphSvgTheming.ApplyCurrentTheme(rawSvg);

            _cache[cacheKey] = themedSvg;
            CurrentSvg = themedSvg;
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load graph {GraphName} for device {DeviceId}", graph.Name, _deviceId);
            ErrorMessage = ex.ToUserMessage();
            CurrentSvg = null;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

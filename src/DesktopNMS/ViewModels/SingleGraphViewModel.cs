using System;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Fetches and themes exactly one fixed graph, once - the shape a compact
/// "at a glance" embed needs (Device Overview's sparkline, issue #11),
/// unlike <see cref="GraphsSectionViewModel"/>'s own picker/time-range/
/// per-window cache, which a single always-the-same-graph embed has no use
/// for. Testing confirmed LibreNMS's graph endpoint ignores
/// only_graph/no_legend (both still render full axes/legend regardless of
/// size), so this is a compact full graph, not a true minimal sparkline.
/// </summary>
public sealed class SingleGraphViewModel : ObservableObject
{
    private readonly int _deviceId;
    private readonly string _graphName;
    private readonly GraphTimeRange _range;
    private readonly int _width;
    private readonly int _height;
    private readonly ILibreNmsClient _client;
    private readonly ILogger _logger;

    private bool _isLoading = true;
    private string? _errorMessage;
    private string? _svg;

    public SingleGraphViewModel(int deviceId, string graphName, GraphTimeRange range, int width, int height, ILibreNmsClient client, ILogger logger)
    {
        _deviceId = deviceId;
        _graphName = graphName;
        _range = range;
        _width = width;
        _height = height;
        _client = client;
        _logger = logger;
    }

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

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var rawSvg = await _client.Graphs.GetSvgAsync(_deviceId, _graphName, _range, _width, _height, cancellationToken).ConfigureAwait(true);
            Svg = GraphSvgTheming.ApplyCurrentTheme(rawSvg);
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            // Not every device has every graph (e.g. a UPS has no ping
            // graph's ICMP counterpart data) - logged at Debug, not
            // Warning, since a missing graph here is routine, not a
            // problem worth surfacing the way the dedicated Graphs
            // section's own failures are.
            _logger.LogDebug(ex, "Could not load the {GraphName} sparkline for device {DeviceId}", _graphName, _deviceId);
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsLoading = false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The port graphs panel under a table of ports - Device Details' Ports
/// tab (#8) and the Neighbours tab: one port's traffic, unicast packets,
/// broadcast/multicast packets and errors side by side, without their
/// legends (a click opens one full size in Graphs, legend and all), with a time range,
/// and a chevron that folds it down to its header line so the table gets
/// the full height (remembered, shared by both). LibreNMS draws the graphs
/// itself - see <see cref="IGraphsApi.GetPortSvgAsync"/>.
/// </summary>
public sealed class PortGraphsPanelViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;

    private int _deviceId;
    private string? _ifName;
    private string? _problem;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private bool _hasPort;
    private int _version;

    public PortGraphsPanelViewModel(ILibreNmsClient client, ISettingsStore settings, ILogger logger)
    {
        _client = client;
        _settings = settings;
        _logger = logger;

        Graphs = new ObservableCollection<PortGraphViewModel>(
            Types.Select(t => new PortGraphViewModel(t.Name, t.Description, Open, AddToDashboard)));

        TimeRange = new GraphTimeRangeViewModel();
        TimeRange.Changed += (_, _) => _ = LoadAsync();
        ToggleCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);
    }

    /// <summary>The port graphs LibreNMS draws for any port (checked live - the rest, like PAgP or FDB count, only exist on some).</summary>
    public static IReadOnlyList<GraphType> Types { get; } =
    [
        new() { Name = "port_bits", Description = "Traffic" },
        new() { Name = "port_upkts", Description = "Unicast packets" },
        new() { Name = "port_nupkts", Description = "Broadcast and multicast packets" },
        new() { Name = "port_errors", Description = "Errors" },
    ];

    public ObservableCollection<PortGraphViewModel> Graphs { get; }

    /// <summary>A graph clicked: open it full size in Graphs, with its legend - (device, ifName, graph type).</summary>
    public event EventHandler<PortGraphOpenRequest>? OpenRequested;

    private void Open(PortGraphViewModel graph)
    {
        if (_ifName is { } ifName)
        {
            OpenRequested?.Invoke(this, new PortGraphOpenRequest(_deviceId, ifName, graph.GraphType));
        }
    }

    /// <summary>A graph's "Add to dashboard" (#285): (device, ifName, graph type) and a title for the widget.</summary>
    public event EventHandler<PortGraphAddRequest>? AddToDashboardRequested;

    private string? _notice;
    private System.Windows.Threading.DispatcherTimer? _noticeTimer;

    /// <summary>"Traffic added to the dashboard" for a few seconds in the header, after Add to dashboard.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => _notice is not null;

    private void AddToDashboard(PortGraphViewModel graph)
    {
        if (_ifName is not { } ifName)
        {
            return;
        }

        AddToDashboardRequested?.Invoke(this, new PortGraphAddRequest(_deviceId, ifName, graph.GraphType, $"{ifName} · {graph.Title}"));

        Notice = $"{graph.Title} added to the dashboard";
        _noticeTimer?.Stop();
        _noticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer?.Stop();
            Notice = null;
        };
        _noticeTimer.Start();
    }

    public GraphTimeRangeViewModel TimeRange { get; }

    public RelayCommand ToggleCommand { get; }

    /// <summary>A port to show - the panel's hidden until one is picked.</summary>
    public bool HasPort
    {
        get => _hasPort;
        private set => SetProperty(ref _hasPort, value);
    }

    /// <summary>The header's bold part - the port, or the neighbour on it.</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>The header's rest - " - port 5 on sw-access-01", " - uplink".</summary>
    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    /// <summary>Folded down to its header line - remembered, quietly (nothing else cares).</summary>
    public bool IsCollapsed
    {
        get => _settings.Current.PortGraphsCollapsed;
        set
        {
            if (_settings.Current.PortGraphsCollapsed == value)
            {
                return;
            }

            _settings.Current.PortGraphsCollapsed = value;
            _settings.SaveQuietly();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToggleGlyph));

            // Folded, nothing was drawn - catch up now it's open.
            if (!value)
            {
                _ = LoadAsync();
            }
        }
    }

    /// <summary>Chevron down while open (fold it away), up while folded (bring it back).</summary>
    public string ToggleGlyph => IsCollapsed ? "" : "";

    /// <summary>
    /// Shows this port's graphs. <paramref name="ifName"/> null means the
    /// port's not known to LibreNMS - <paramref name="problem"/> says so in
    /// place of the graphs.
    /// </summary>
    public void Show(int deviceId, string? ifName, string title, string subtitle, string? problem = null)
    {
        _deviceId = deviceId;
        _ifName = string.IsNullOrWhiteSpace(ifName) ? null : ifName;
        _problem = problem;
        Title = title;
        Subtitle = subtitle;
        HasPort = true;
        _ = LoadAsync();
    }

    public void Clear()
    {
        _version++;
        HasPort = false;
        _ifName = null;
        foreach (var graph in Graphs)
        {
            graph.Show(null, null);
        }
    }

    /// <summary>Every graph at once; a newer port or time range wins over one still loading.</summary>
    private async Task LoadAsync()
    {
        var version = ++_version;

        // Folded away: nothing to draw them in - they load when it's opened.
        if (!HasPort || IsCollapsed)
        {
            return;
        }

        if (_ifName is not { } ifName)
        {
            foreach (var graph in Graphs)
            {
                graph.Show(null, _problem ?? "LibreNMS doesn't know this port.");
            }

            return;
        }

        var deviceId = _deviceId;
        var range = TimeRange.ToTimeRange();

        async Task LoadOne(PortGraphViewModel graph)
        {
            graph.BeginLoad();
            try
            {
                var svg = await _client.Graphs.GetPortSvgAsync(deviceId, ifName, graph.GraphType, range, width: 560, height: 150, legend: false).ConfigureAwait(true);
                if (version == _version)
                {
                    graph.Show(GraphSvgTheming.ApplyCurrentTheme(svg), null);
                }
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogWarning(ex, "Could not load the {GraphType} graph for port {IfName} on device {DeviceId}", graph.GraphType, ifName, deviceId);
                if (version == _version)
                {
                    graph.Show(null, ex.ToUserMessage());
                }
            }
        }

        await Task.WhenAll(Graphs.Select(LoadOne)).ConfigureAwait(true);
    }
}

/// <summary>One of the port graphs panel's graphs.</summary>
public sealed class PortGraphViewModel : ObservableObject
{
    private string? _svg;
    private bool _isLoading;
    private string? _errorMessage;

    public PortGraphViewModel(string graphType, string title, Action<PortGraphViewModel>? open = null, Action<PortGraphViewModel>? addToDashboard = null)
    {
        GraphType = graphType;
        Title = title;
        OpenCommand = new RelayCommand(() => open?.Invoke(this), () => open is not null && Svg is not null);
        AddToDashboardCommand = new RelayCommand(() => addToDashboard?.Invoke(this), () => addToDashboard is not null);
    }

    /// <summary>Opens this graph full size in Graphs.</summary>
    public RelayCommand OpenCommand { get; }

    /// <summary>Puts this graph on the dashboard as a Graph widget (#285).</summary>
    public RelayCommand AddToDashboardCommand { get; }

    /// <summary>LibreNMS's graph name, e.g. "port_errors".</summary>
    public string GraphType { get; }

    public string Title { get; }

    public string? Svg
    {
        get => _svg;
        private set
        {
            if (SetProperty(ref _svg, value))
            {
                OpenCommand.RaiseCanExecuteChanged();
            }
        }
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

/// <summary>A port graph to open full size in Graphs.</summary>
public sealed record PortGraphOpenRequest(int DeviceId, string IfName, string GraphType);

/// <summary>A port graph to put on the dashboard (#285), with the widget's title.</summary>
public sealed record PortGraphAddRequest(int DeviceId, string IfName, string GraphType, string Title);

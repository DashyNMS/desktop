using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// What the Top interfaces, Top errors and Top devices dashboard widgets
/// (#199-#201) share: every port in the fleet from <see cref="IFleetPorts"/>,
/// refreshed with each device poll and the Dashboard's refresh button, ranked
/// by a subclass into rows; and the options strip - In / Out / Total, how
/// many rows - saved per widget, so several Top widgets with different
/// options can sit on one dashboard.
/// </summary>
public abstract class TopWidgetViewModel : DashboardWidgetViewModel, IDisposable
{
    public static readonly IReadOnlyList<int> CountChoices = new[] { 3, 5, 10 };

    public const int DefaultCount = 3;

    private readonly IDashboardLayoutService _layout;
    private readonly IFleetPorts _ports;
    private readonly IDeviceCache _devices;
    private readonly ISettingsStore _settings;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;

    private IReadOnlyList<Port> _latest = Array.Empty<Port>();
    private int _count;
    private RankBy _rankBy;
    private bool _hideQuiet;
    private bool _isLoading;
    private bool _hasLoaded;
    private string? _errorMessage;
    private int _loadVersion;

    protected TopWidgetViewModel(
        IDashboardLayoutService layout,
        DashboardWidget model,
        IFleetPorts ports,
        IDeviceCache devices,
        ISettingsStore settings,
        DeviceMonitor deviceMonitor,
        ILogger logger,
        Action<TopRowViewModel> open)
        : base(layout, model)
    {
        _layout = layout;
        _ports = ports;
        _devices = devices;
        _settings = settings;
        _deviceMonitor = deviceMonitor;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _count = CountChoices.Contains(model.TopCount) ? model.TopCount : DefaultCount;
        _rankBy = model.TopRankBy;
        _hideQuiet = model.TopHideQuiet;

        Rows = new ObservableCollection<TopRowViewModel>();
        Header = new TopWidgetHeader(this);
        RankByInCommand = new RelayCommand(() => SetRankBy(_rankBy == RankBy.In ? RankBy.Total : RankBy.In));
        RankByOutCommand = new RelayCommand(() => SetRankBy(_rankBy == RankBy.Out ? RankBy.Total : RankBy.Out));
        OpenCommand = new RelayCommand(parameter =>
        {
            if (parameter is TopRowViewModel row)
            {
                open(row);
            }
        });

        // Refresh with each device poll, like the Wireless widget. DeviceMonitor
        // is lazily started, and this may be the only thing asking for it.
        _deviceMonitor.Polled += OnDevicesPolled;
        _deviceMonitor.Start();
        _ = LoadAsync(refresh: false);
    }

    public ObservableCollection<TopRowViewModel> Rows { get; }

    public RelayCommand OpenCommand { get; }

    /// <summary>"In", "Out" or "Total" in the strip's column headers - errors say "errors/s", traffic says nothing extra.</summary>
    public abstract string EmptyText { get; }

    /// <summary>The Top errors widget's extra "Hide quiet ports" chip.</summary>
    public virtual bool OffersHideQuiet => false;

    /// <summary>Under the rows: what the numbers are, e.g. "Total port traffic, not throughput" for devices.</summary>
    public virtual string? FootnoteText => null;

    public bool HasFootnote => FootnoteText is not null;

    /// <summary>The title bar's "Top 3 ▾" dropdown and, for errors, its eye - see <see cref="TopWidgetHeader"/>.</summary>
    public override object? HeaderOptions => Header;

    public TopWidgetHeader Header { get; }

    /// <summary>How many rows - the title bar's dropdown.</summary>
    public int SelectedCount
    {
        get => _count;
        set
        {
            if (CountChoices.Contains(value) && SetProperty(ref _count, value))
            {
                SaveOptions();
            }
        }
    }

    /// <summary>The first column's heading: "Device · interface", or just "Device".</summary>
    public virtual string NameHeading => "Device · interface";

    /// <summary>Clicking the In heading: rank by In, or - if it already is - by Total.</summary>
    public RelayCommand RankByInCommand { get; }

    /// <summary>Clicking the Out heading: rank by Out, or - if it already is - by Total.</summary>
    public RelayCommand RankByOutCommand { get; }

    /// <summary>The In heading is highlighted with its arrow: ranking by In, or by Total (both).</summary>
    public bool IsInRanked => _rankBy is RankBy.In or RankBy.Total;

    public bool IsOutRanked => _rankBy is RankBy.Out or RankBy.Total;

    /// <summary>"Ranked by in", "... out", "... total - click In or Out to rank by one" - the headings' tooltip.</summary>
    public string RankText => _rankBy switch
    {
        RankBy.In => "Ranked by in. Click In again to rank by total.",
        RankBy.Out => "Ranked by out. Click Out again to rank by total.",
        _ => "Ranked by total. Click In or Out to rank by one.",
    };

    public bool HideQuiet
    {
        get => _hideQuiet;
        set
        {
            if (SetProperty(ref _hideQuiet, value))
            {
                SaveOptions();
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
                RaiseStateChanged();
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
                RaiseStateChanged();
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public bool HasRows => Rows.Count > 0;

    /// <summary>Only before the first result - later refreshes update in place.</summary>
    public bool ShowLoading => IsLoading && !_hasLoaded;

    public bool ShowEmptyMessage => _hasLoaded && !IsLoading && !HasRows && !HasError;

    protected RankBy RankBy => _rankBy;

    protected int Count => _count;

    /// <summary>The rows for these ports, in order, at most <see cref="Count"/>.</summary>
    protected abstract IReadOnlyList<TopRowViewModel> BuildRows(IReadOnlyList<Port> ports);

    /// <summary>The Dashboard's refresh button: fetch again, whether or not the last copy is fresh.</summary>
    public void Reload() => _ = LoadAsync(refresh: true);

    protected string DeviceName(int deviceId)
    {
        var device = _devices.Get(deviceId);
        return device is null ? "Device " + deviceId.ToString(CultureInfo.CurrentCulture) : _settings.Current.DeviceNameStyle.Resolve(device, device.Hostname);
    }

    protected DeviceState? DeviceStateOf(int deviceId) => _devices.Get(deviceId)?.State;

    /// <summary>"1.2 Gbps", "840 Kbps" - LibreNMS's own style for rates.</summary>
    protected static string FormatBits(double bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "0";
        }

        string[] units = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        var value = bitsPerSecond;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>"0.4/s", "12/s".</summary>
    protected static string FormatPerSecond(double perSecond)
        => perSecond <= 0 ? "0" : perSecond.ToString(perSecond < 10 ? "0.##" : "0", CultureInfo.CurrentCulture) + "/s";

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (result.Succeeded)
        {
            _dispatcher.InvokeAsync(() => _ = LoadAsync(refresh: false));
        }
    }

    private async Task LoadAsync(bool refresh)
    {
        var version = ++_loadVersion;
        IsLoading = true;

        try
        {
            var ports = await _ports.GetAsync(refresh).ConfigureAwait(true);
            if (version != _loadVersion)
            {
                return;
            }

            _latest = ports;
            ErrorMessage = null;
            Apply();
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load the fleet's ports for {Widget}", Title);
            if (version == _loadVersion)
            {
                // The last list stays on screen under the error.
                ErrorMessage = "Couldn't reach LibreNMS for port data.";
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                _hasLoaded = true;
                IsLoading = false;
            }
        }
    }

    private void Apply()
    {
        Rows.Clear();
        foreach (var row in BuildRows(_latest))
        {
            Rows.Add(row);
        }

        RaiseStateChanged();
    }

    private void SetRankBy(RankBy rankBy)
    {
        if (_rankBy == rankBy)
        {
            return;
        }

        _rankBy = rankBy;
        OnPropertyChanged(nameof(IsInRanked));
        OnPropertyChanged(nameof(IsOutRanked));
        OnPropertyChanged(nameof(RankText));
        SaveOptions();
    }

    private void SaveOptions()
    {
        _layout.SetTopOptions(Id, _count, _rankBy, _hideQuiet);
        Apply();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowEmptyMessage));
    }

    public void Dispose() => _deviceMonitor.Polled -= OnDevicesPolled;
}

/// <summary>
/// What a Top widget puts in its title bar (#199-#201): the "Top 3 ▾" count
/// and, for errors, the eye that hides quiet ports. Its own type so the
/// title bar can template it without re-drawing the whole widget.
/// </summary>
public sealed class TopWidgetHeader
{
    public TopWidgetHeader(TopWidgetViewModel widget) => Widget = widget;

    public TopWidgetViewModel Widget { get; }

    public IReadOnlyList<int> CountChoices => TopWidgetViewModel.CountChoices;
}

/// <summary>One row in a Top widget: a device, perhaps one of its ports, and the two numbers - with a bar for how much of the port (or the list's top row) it is.</summary>
public sealed class TopRowViewModel
{
    public TopRowViewModel(int deviceId, int? portId, string title, string? subtitle, string inText, string outText, double barFraction, DeviceState? deviceState, bool isWarning = false, bool isCritical = false)
    {
        DeviceId = deviceId;
        PortId = portId;
        Title = title;
        Subtitle = subtitle;
        InText = inText;
        OutText = outText;
        BarFraction = Math.Clamp(barFraction, 0, 1);
        DeviceState = deviceState;
        IsWarning = isWarning && !isCritical;
        IsCritical = isCritical;
    }

    public int DeviceId { get; }

    public int? PortId { get; }

    public string Title { get; }

    public string? Subtitle { get; }

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    public string InText { get; }

    public string OutText { get; }

    /// <summary>0-1: for a port, how full it is against its speed; for a device, against the top row.</summary>
    public double BarFraction { get; }

    /// <summary>Bar width out of 100, for a star-sized grid column pair.</summary>
    public double BarPercent => BarFraction * 100;

    public double BarRemainderPercent => 100 - BarPercent;

    public DeviceState? DeviceState { get; }

    public bool HasDeviceState => DeviceState is not null;

    /// <summary>For an error row: any errors at all.</summary>
    public bool IsWarning { get; }

    /// <summary>For an error row: past the critical threshold.</summary>
    public bool IsCritical { get; }
}

/// <summary>Top interfaces (#199): the busiest ports in the fleet, with how full each is against its speed.</summary>
public sealed class TopInterfacesWidgetViewModel : TopWidgetViewModel
{
    public TopInterfacesWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, IFleetPorts ports, IDeviceCache devices, ISettingsStore settings, DeviceMonitor deviceMonitor, ILogger logger, Action<TopRowViewModel> open)
        : base(layout, model, ports, devices, settings, deviceMonitor, logger, open)
    {
    }

    public override string EmptyText => "No traffic on any interface.";

    protected override IReadOnlyList<TopRowViewModel> BuildRows(IReadOnlyList<Port> ports)
        => PortRankings.TopTraffic(ports, RankBy, Count)
            .Select(r =>
            {
                var speed = r.Port.IfSpeed is > 0 ? r.Port.IfSpeed.Value : 0;
                var busiest = RankBy switch { RankBy.In => r.In, RankBy.Out => r.Out, _ => Math.Max(r.In, r.Out) };
                return new TopRowViewModel(
                    r.Port.DeviceId,
                    r.Port.PortId,
                    DeviceName(r.Port.DeviceId),
                    PortLabel(r.Port),
                    FormatBits(r.In),
                    FormatBits(r.Out),
                    speed > 0 ? busiest / speed : 0,
                    DeviceStateOf(r.Port.DeviceId));
            })
            .ToList();

    internal static string PortLabel(Port port)
        => string.IsNullOrWhiteSpace(port.IfAlias) || string.Equals(port.IfAlias, port.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? port.DisplayName
            : port.DisplayName + " · " + port.IfAlias;
}

/// <summary>Top errors (#200): the ports with the most errors per second, amber for any, red past a threshold.</summary>
public sealed class TopErrorsWidgetViewModel : TopWidgetViewModel
{
    /// <summary>Errors per second (in or out) at which a row turns red.</summary>
    private const double CriticalErrorsPerSecond = 1;

    public TopErrorsWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, IFleetPorts ports, IDeviceCache devices, ISettingsStore settings, DeviceMonitor deviceMonitor, ILogger logger, Action<TopRowViewModel> open)
        : base(layout, model, ports, devices, settings, deviceMonitor, logger, open)
    {
    }

    public override string EmptyText => "No interface errors.";

    public override bool OffersHideQuiet => true;

    protected override IReadOnlyList<TopRowViewModel> BuildRows(IReadOnlyList<Port> ports)
    {
        var ranked = PortRankings.TopErrors(ports, RankBy, Count, includeZero: !HideQuiet);
        var worst = ranked.Count == 0 ? 0 : ranked.Max(r => Math.Max(r.In, r.Out));

        return ranked
            .Select(r => new TopRowViewModel(
                r.Port.DeviceId,
                r.Port.PortId,
                DeviceName(r.Port.DeviceId),
                TopInterfacesWidgetViewModel.PortLabel(r.Port),
                FormatPerSecond(r.In),
                FormatPerSecond(r.Out),
                worst > 0 ? Math.Max(r.In, r.Out) / worst : 0,
                DeviceStateOf(r.Port.DeviceId),
                isWarning: r.Total > 0,
                isCritical: Math.Max(r.In, r.Out) >= CriticalErrorsPerSecond))
            .ToList();
    }
}

/// <summary>Top devices (#201): the devices moving the most traffic, their ports summed.</summary>
public sealed class TopDevicesWidgetViewModel : TopWidgetViewModel
{
    public TopDevicesWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, IFleetPorts ports, IDeviceCache devices, ISettingsStore settings, DeviceMonitor deviceMonitor, ILogger logger, Action<TopRowViewModel> open)
        : base(layout, model, ports, devices, settings, deviceMonitor, logger, open)
    {
    }

    public override string EmptyText => "No traffic on any device.";

    public override string NameHeading => "Device";

    /// <summary>Traffic passing through a device shows on two of its ports, so the sum isn't throughput - say so.</summary>
    public override string? FootnoteText => "Total port traffic: traffic passing through a device counts on both ports.";

    protected override IReadOnlyList<TopRowViewModel> BuildRows(IReadOnlyList<Port> ports)
    {
        var ranked = PortRankings.TopDevices(ports, RankBy, Count);
        double Value(RankedDevice d) => RankBy switch { RankBy.In => d.In, RankBy.Out => d.Out, _ => d.Total };
        var top = ranked.Count == 0 ? 0 : ranked.Max(Value);

        return ranked
            .Select(d => new TopRowViewModel(
                d.DeviceId,
                portId: null,
                DeviceName(d.DeviceId),
                d.ActivePorts == 1 ? "1 active port" : d.ActivePorts.ToString(CultureInfo.CurrentCulture) + " active ports",
                FormatBits(d.In),
                FormatBits(d.Out),
                top > 0 ? Value(d) / top : 0,
                DeviceStateOf(d.DeviceId)))
            .ToList();
    }
}

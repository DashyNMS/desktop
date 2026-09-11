using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>Which section of the device window is showing.</summary>
public enum DeviceDetailSection
{
    Overview,
    Sensors,
    Ports,
    AlertHistory,
}

/// <summary>
/// View model behind a single device's detail window (see <see cref="Views.DeviceView"/>),
/// opened from the Devices tab instead of jumping straight to the LibreNMS
/// website. Device and sensor data both come from the already-running shared
/// <see cref="DeviceMonitor"/>/<see cref="SensorMonitor"/> - this window never
/// starts its own poll of either - and are simply filtered down to this one
/// device id. Alert history (<c>/api/v0/logs/alertlog</c>) is genuinely
/// per-device already, so it is fetched once when the window opens.
/// </summary>
public sealed class DeviceDetailViewModel : ObservableObject, IDisposable
{
    private readonly int _deviceId;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly SensorMonitor _sensorMonitor;
    private readonly AlertMonitor _alertMonitor;
    private readonly ILibreNmsClient _client;
    private readonly IAlertRuleCache _ruleFields;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILogger<DeviceDetailViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, SensorItemViewModel> _sensorIndex = new();
    private readonly Dictionary<int, AlertRule?> _ruleCache = new();

    private Device? _device;
    private bool _isUnderMaintenance;
    private bool _isBusy;
    private string? _errorMessage;
    private DeviceDetailSection _selectedSection = DeviceDetailSection.Overview;

    public DeviceDetailViewModel(
        int deviceId,
        DeviceMonitor deviceMonitor,
        SensorMonitor sensorMonitor,
        AlertMonitor alertMonitor,
        IDeviceCache deviceCache,
        ILibreNmsClient client,
        IAlertRuleCache ruleFields,
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        ILogger<DeviceDetailViewModel> logger)
    {
        _deviceId = deviceId;
        _deviceMonitor = deviceMonitor;
        _sensorMonitor = sensorMonitor;
        _alertMonitor = alertMonitor;
        _client = client;
        _ruleFields = ruleFields;
        _session = session;
        _settings = settings;
        _windows = windows;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Sensors = new ObservableCollection<SensorItemViewModel>();
        AlertHistory = new ObservableCollection<AlertLogItemViewModel>();
        ActiveAlerts = new ObservableCollection<ActiveAlertItemViewModel>();
        Ports = new ObservableCollection<PortItemViewModel>();

        OpenInLibreNmsCommand = new RelayCommand(() =>
        {
            if (DeviceUrl is { } url)
            {
                _windows.OpenUrl(url);
            }
        }, () => DeviceUrl is not null);

        ShowAlertsCommand = new RelayCommand(() => _windows.ShowAlertsForDevice(_device?.Hostname ?? Name));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _session.IsConnected && !IsBusy);

        SelectOverviewCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Overview);
        SelectSensorsCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Sensors);
        SelectPortsCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Ports);
        SelectAlertHistoryCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.AlertHistory);

        // Shows whatever is already cached instantly, rather than a blank
        // window until the next shared poll lands.
        _device = deviceCache.Get(deviceId);

        _deviceMonitor.Polled += OnDevicePolled;
        _sensorMonitor.Polled += OnSensorPolled;
        _alertMonitor.Polled += OnAlertsPolled;

        // All three monitors are almost certainly already running - the
        // Devices tab that opened this window depends on DeviceMonitor, and
        // AlertMonitor starts at sign-in - but Start() is idempotent, and a
        // Sensors widget being the only prior consumer of SensorMonitor
        // should not leave this window's sensor list empty.
        _deviceMonitor.Start();
        _sensorMonitor.Start();
        _alertMonitor.Start();
        _deviceMonitor.RequestRefresh();
        _sensorMonitor.RequestRefresh();
        _alertMonitor.RequestRefresh();

        _ = LoadAlertHistoryAsync();
        _ = LoadPortsAsync();
    }

    public ObservableCollection<SensorItemViewModel> Sensors { get; }

    public ObservableCollection<AlertLogItemViewModel> AlertHistory { get; }

    public ObservableCollection<ActiveAlertItemViewModel> ActiveAlerts { get; }

    public ObservableCollection<PortItemViewModel> Ports { get; }

    public RelayCommand OpenInLibreNmsCommand { get; }

    public RelayCommand ShowAlertsCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand SelectOverviewCommand { get; }

    public RelayCommand SelectSensorsCommand { get; }

    public RelayCommand SelectPortsCommand { get; }

    public RelayCommand SelectAlertHistoryCommand { get; }

    public DeviceDetailSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(IsOverviewSelected));
                OnPropertyChanged(nameof(IsSensorsSelected));
                OnPropertyChanged(nameof(IsPortsSelected));
                OnPropertyChanged(nameof(IsAlertHistorySelected));
            }
        }
    }

    public bool IsOverviewSelected => SelectedSection == DeviceDetailSection.Overview;

    public bool IsSensorsSelected => SelectedSection == DeviceDetailSection.Sensors;

    public bool IsPortsSelected => SelectedSection == DeviceDetailSection.Ports;

    public bool IsAlertHistorySelected => SelectedSection == DeviceDetailSection.AlertHistory;

    // ------------------------------------------------------------------ device

    public int DeviceId => _deviceId;

    public string Name => _device is null
        ? $"Device {_deviceId}"
        : _settings.Current.DeviceNameStyle.Resolve(_device, _device.Hostname);

    public string? AlternateName => _device is null
        ? null
        : _settings.Current.DeviceNameStyle.ResolveSecondary(_device, _device.Hostname, Name);

    public bool HasAlternateName => AlternateName is not null;

    public DeviceState State => _isUnderMaintenance ? DeviceState.Maintenance : _device?.State ?? DeviceState.Down;

    public string StateText => State.ToDisplayString();

    public string Ip => Blank(_device?.Ip);

    public string Os => Blank(_device?.Os);

    public string Hardware => Blank(_device?.Hardware);

    public string Location => Blank(_device?.Location);

    public string Type => Blank(_device?.Type);

    public string UptimeText => _device is { State: DeviceState.Up } d ? FormatUptime(d.Uptime) : "-";

    public Uri? DeviceUrl => _device is null ? null : _session.Connection?.DeviceUrl(_deviceId);

    /// <summary>True once the shared device monitor has actually reported on this device at least once.</summary>
    public bool HasLoaded => _device is not null;

    /// <summary>True until the first device poll lands, so the header can say so instead of showing blank fields.</summary>
    public bool IsLoadingDevice => !HasLoaded;

    public bool HasSensors => Sensors.Count > 0;

    public int SensorWarningCount => Sensors.Count(s => s.Severity == AlertSeverity.Warning);

    public int SensorCriticalCount => Sensors.Count(s => s.Severity == AlertSeverity.Critical);

    /// <summary>"2 critical, 1 warning", or empty when nothing is out of range - a quick-glance summary for the Overview card.</summary>
    public string SensorAlertSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (SensorCriticalCount > 0) parts.Add($"{SensorCriticalCount} critical");
            if (SensorWarningCount > 0) parts.Add($"{SensorWarningCount} warning");
            return string.Join(", ", parts);
        }
    }

    public bool HasAlertHistory => AlertHistory.Count > 0;

    /// <summary>
    /// True once ports have actually been fetched and the device reports at
    /// least one - many devices (UPS units, cameras, appliances) have no SNMP
    /// interfaces at all, so the Ports tab only shows up for devices that have them.
    /// </summary>
    public bool HasPorts => Ports.Count > 0;

    public int PortsUpCount => Ports.Count(p => p.IsUp);

    public int PortsDownCount => Ports.Count(p => !p.IsUp);

    public bool HasActiveAlerts => ActiveAlerts.Count > 0;

    public int ActiveCriticalCount => ActiveAlerts.Count(a => a.Severity == AlertSeverity.Critical);

    public int ActiveWarningCount => ActiveAlerts.Count(a => a.Severity == AlertSeverity.Warning);

    /// <summary>"2 critical, 1 warning", or empty - a quick-glance summary next to the Overview's Active alerts header.</summary>
    public string ActiveAlertSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (ActiveCriticalCount > 0) parts.Add($"{ActiveCriticalCount} critical");
            if (ActiveWarningCount > 0) parts.Add($"{ActiveWarningCount} warning");
            return string.Join(", ", parts);
        }
    }

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

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    // --------------------------------------------------------------- handlers

    private void OnDevicePolled(object? sender, DevicePollResult result) => _dispatcher.InvokeAsync(() =>
    {
        if (!result.Succeeded)
        {
            return;
        }

        var device = result.Devices.FirstOrDefault(d => d.DeviceId == _deviceId);
        if (device is null)
        {
            // Removed from LibreNMS since the window opened - leave the last
            // known detail showing rather than blanking the window out.
            return;
        }

        _device = device;
        _isUnderMaintenance = result.DeviceIdsUnderMaintenance.Contains(_deviceId);
        RaiseDeviceChanged();
    });

    private void OnSensorPolled(object? sender, SensorPollResult result) => _dispatcher.InvokeAsync(() =>
    {
        if (result.Succeeded)
        {
            ApplySensors(result.Sensors);
        }
    });

    /// <summary>
    /// AlertMonitor polls the whole fleet, same as everywhere else in the app
    /// that needs alerts - there is no per-device filter on the server side -
    /// so this just picks out the rows for this one device from what it
    /// already fetched, at no extra API cost.
    /// </summary>
    private void OnAlertsPolled(object? sender, AlertPollResult result) => _dispatcher.InvokeAsync(() =>
    {
        if (result.Succeeded)
        {
            ApplyActiveAlerts(result.Alerts);
        }
    });

    private void ApplyActiveAlerts(IReadOnlyList<Alert> fleet)
    {
        var serverTimestampsAreUtc = _settings.Current.ServerTimestampsAreUtc;

        var mine = fleet
            .Where(a => a.DeviceId == _deviceId && a.State is AlertState.Active or AlertState.Acknowledged)
            .OrderByDescending(a => a.Severity.SortRank())
            .ThenByDescending(a => a.Timestamp)
            .Select(a => new ActiveAlertItemViewModel(a, serverTimestampsAreUtc))
            .ToList();

        ActiveAlerts.Clear();
        foreach (var alert in mine)
        {
            ActiveAlerts.Add(alert);
        }

        OnPropertyChanged(nameof(HasActiveAlerts));
        OnPropertyChanged(nameof(ActiveCriticalCount));
        OnPropertyChanged(nameof(ActiveWarningCount));
        OnPropertyChanged(nameof(ActiveAlertSummaryText));
    }

    private void ApplySensors(IReadOnlyList<Sensor> fleet)
    {
        var settings = _settings.Current;
        var connection = _session.Connection;
        var deviceName = Name;

        var mine = fleet
            .Where(s => s.DeviceId == _deviceId)
            .OrderBy(s => s.SensorClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var incoming = mine.Select(s => s.SensorId).ToHashSet();

        for (var i = Sensors.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Sensors[i].SensorId))
            {
                _sensorIndex.Remove(Sensors[i].SensorId);
                Sensors.RemoveAt(i);
            }
        }

        for (var target = 0; target < mine.Count; target++)
        {
            var sensor = mine[target];
            var (evaluator, unit) = ResolveThresholds(sensor.SensorClass, settings);

            if (_sensorIndex.TryGetValue(sensor.SensorId, out var existing))
            {
                existing.Update(sensor, deviceName, connection, evaluator);

                var currentIndex = Sensors.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != target && target < Sensors.Count)
                {
                    Sensors.Move(currentIndex, target);
                }
            }
            else
            {
                var item = new SensorItemViewModel(sensor, deviceName, connection, evaluator, unit);
                _sensorIndex[sensor.SensorId] = item;
                Sensors.Insert(Math.Min(target, Sensors.Count), item);
            }
        }

        OnPropertyChanged(nameof(HasSensors));
        OnPropertyChanged(nameof(SensorWarningCount));
        OnPropertyChanged(nameof(SensorCriticalCount));
        OnPropertyChanged(nameof(SensorAlertSummaryText));
    }

    /// <summary>
    /// Unlike the Health tab and the Dashboard's Sensors widget, this shows
    /// every sensor class LibreNMS reports for the device - not just the four
    /// with configured thresholds - since the point here is a complete picture
    /// of one device. Classes outside <see cref="SensorCategoryRegistry"/> just
    /// show their raw value with no severity colouring.
    /// </summary>
    private static (IThresholdEvaluator Evaluator, string UnitSuffix) ResolveThresholds(string? sensorClass, AppSettings settings)
    {
        var entry = SensorCategoryRegistry.Resolve(sensorClass);
        return entry is not null ? (entry.Thresholds(settings), entry.UnitSuffix) : (NoThresholds.Instance, string.Empty);
    }

    private async Task LoadAlertHistoryAsync()
    {
        IsBusy = true;

        try
        {
            var entries = await _client.Logs.ListAlertLogAsync(_deviceId, 30).ConfigureAwait(true);
            var ruleIds = entries.Select(e => e.RuleId).Distinct().ToList();

            await Task.WhenAll(ruleIds.Select(EnsureRuleCachedAsync)).ConfigureAwait(true);

            // The rule tells us which columns its condition actually tests; the
            // log entry's own "details" blob has the values - same two-piece
            // lookup the main Alerts tab uses for its fault view.
            var fieldsByRule = new Dictionary<int, IReadOnlySet<string>>();
            foreach (var ruleId in ruleIds)
            {
                fieldsByRule[ruleId] = await _ruleFields.GetConditionFieldsAsync(ruleId).ConfigureAwait(true);
            }

            AlertHistory.Clear();
            foreach (var entry in entries)
            {
                _ruleCache.TryGetValue(entry.RuleId, out var rule);
                fieldsByRule.TryGetValue(entry.RuleId, out var fields);
                var detail = AlertFaultParser.Parse(entry, fields);
                AlertHistory.Add(new AlertLogItemViewModel(entry, rule, detail));
            }

            OnPropertyChanged(nameof(HasAlertHistory));
            ErrorMessage = null;
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load alert history for device {DeviceId}", _deviceId);
            ErrorMessage = ex.ToUserMessage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load alert history for device {DeviceId}", _deviceId);
            ErrorMessage = "Could not load alert history.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnsureRuleCachedAsync(int ruleId)
    {
        if (_ruleCache.ContainsKey(ruleId))
        {
            return;
        }

        try
        {
            _ruleCache[ruleId] = await _client.Rules.GetAsync(ruleId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The log entry is still shown, just without a friendly rule name.
            _logger.LogDebug(ex, "Could not read alert rule {RuleId}", ruleId);
            _ruleCache[ruleId] = null;
        }
    }

    /// <summary>
    /// LibreNMS has no fleet-wide ports endpoint, only per device, so unlike
    /// sensors/devices/alerts this is fetched fresh rather than filtered from
    /// a shared poller. A failure is logged and quietly leaves the list empty
    /// - many devices genuinely have no SNMP interfaces at all, and treating
    /// that the same as an error would raise a false alarm on every one of them.
    /// </summary>
    private async Task LoadPortsAsync()
    {
        try
        {
            var ports = await _client.Ports.ListForDeviceAsync(_deviceId).ConfigureAwait(true);

            // Neighbours are fetched independently and tolerate their own
            // failure - a problem with link discovery (a different endpoint,
            // possibly unsupported on an older LibreMS version) should not
            // take the ports list down with it.
            var linksByPort = (await TryLoadLinksAsync().ConfigureAwait(true))
                .Where(l => l.LocalPortId > 0)
                .GroupBy(l => l.LocalPortId)
                .ToDictionary(g => g.Key, g => g.First());

            Ports.Clear();
            foreach (var port in ports.OrderBy(p => p.IfIndex ?? int.MaxValue))
            {
                linksByPort.TryGetValue(port.PortId, out var link);
                Ports.Add(new PortItemViewModel(port, link, _windows));
            }

            OnPropertyChanged(nameof(HasPorts));
            OnPropertyChanged(nameof(PortsUpCount));
            OnPropertyChanged(nameof(PortsDownCount));
        }
        catch (LibreNmsApiException ex)
        {
            // Warning, not Debug: a genuine fetch failure (bad request, timeout,
            // server error) should be visible in the log, not indistinguishable
            // from the ordinary case of a device that simply has no ports.
            // ServerMessage (LibreNMS's own explanation, e.g. which column name
            // it rejected) is logged explicitly since ex.Message alone is just
            // the generic "HTTP 400" wrapper - see LibreNmsApiException.
            _logger.LogWarning(ex, "Could not load ports for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load ports for device {DeviceId}", _deviceId);
        }
    }

    private async Task<IReadOnlyList<NetworkLink>> TryLoadLinksAsync()
    {
        try
        {
            return await _client.Links.ListForDeviceAsync(_deviceId).ConfigureAwait(true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load neighbours for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
            return Array.Empty<NetworkLink>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load neighbours for device {DeviceId}", _deviceId);
            return Array.Empty<NetworkLink>();
        }
    }

    private Task RefreshAsync()
    {
        _deviceMonitor.RequestRefresh();
        _sensorMonitor.RequestRefresh();
        _alertMonitor.RequestRefresh();
        return Task.WhenAll(LoadAlertHistoryAsync(), LoadPortsAsync());
    }

    private void RaiseDeviceChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AlternateName));
        OnPropertyChanged(nameof(HasAlternateName));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Ip));
        OnPropertyChanged(nameof(Os));
        OnPropertyChanged(nameof(Hardware));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(DeviceUrl));
        OnPropertyChanged(nameof(HasLoaded));
        OnPropertyChanged(nameof(IsLoadingDevice));
        OpenInLibreNmsCommand.RaiseCanExecuteChanged();
    }

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value!;

    private static string FormatUptime(long seconds)
    {
        if (seconds <= 0)
        {
            return "-";
        }

        var span = TimeSpan.FromSeconds(seconds);

        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    public void Dispose()
    {
        _deviceMonitor.Polled -= OnDevicePolled;
        _sensorMonitor.Polled -= OnSensorPolled;
        _alertMonitor.Polled -= OnAlertsPolled;
    }

    private sealed class NoThresholds : IThresholdEvaluator
    {
        public static readonly NoThresholds Instance = new();

        public AlertSeverity Evaluate(double value) => AlertSeverity.Unknown;
    }
}

/// <summary>Common IANAifType values translated to what LibreNMS's own UI calls them, since the raw MIB enum name is not user-friendly.</summary>
file static class IfTypeDisplay
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ethernetCsmacd"] = "Ethernet",
        ["ieee8023adLag"] = "Link aggregate",
        ["l2vlan"] = "L2 VLAN",
        ["propVirtual"] = "Virtual",
        ["softwareLoopback"] = "Loopback",
        ["tunnel"] = "Tunnel",
        ["other"] = "Other",
    };

    public static string Resolve(string? ifType) =>
        string.IsNullOrWhiteSpace(ifType) ? "-" : Names.GetValueOrDefault(ifType, ifType);
}

/// <summary>One row in a device's Ports tab - one network interface.</summary>
public sealed class PortItemViewModel
{
    private readonly Port _port;
    private readonly NetworkLink? _link;
    private readonly IWindowService _windows;

    public PortItemViewModel(Port port, NetworkLink? link, IWindowService windows)
    {
        _port = port;
        _link = link;
        _windows = windows;

        OpenNeighborCommand = new RelayCommand(
            () => _windows.ShowDeviceDetail(_link!.RemoteDeviceId!.Value),
            () => _link?.RemoteDeviceId is > 0);
    }

    public Port Model => _port;

    public string DisplayName => _port.DisplayName;

    /// <summary>The operator's own description, shown as a subtitle under the port's identity when set and distinct from it.</summary>
    public string? SecondaryName => !string.IsNullOrWhiteSpace(_port.IfAlias)
        && !string.Equals(_port.IfAlias, DisplayName, StringComparison.OrdinalIgnoreCase)
        ? _port.IfAlias
        : null;

    public bool HasSecondaryName => SecondaryName is not null;

    public bool IsUp => _port.IsUp;

    public bool HasKnownStatus => !string.IsNullOrWhiteSpace(_port.IfOperStatus);

    /// <summary>Reuses the app's existing severity colours: unknown status reads as neutral, not as if it were down.</summary>
    public AlertSeverity StatusSeverity => !HasKnownStatus ? AlertSeverity.Unknown : (IsUp ? AlertSeverity.Ok : AlertSeverity.Critical);

    public string StatusText => HasKnownStatus ? Capitalise(_port.IfOperStatus!) : "Unknown";

    public string SpeedText => FormatBitsPerSecond(_port.IfSpeed);

    public string InRateText => _port.IfInOctetsRate is { } rate ? FormatBitsPerSecond((long)(rate * 8)) : "-";

    public string OutRateText => _port.IfOutOctetsRate is { } rate ? FormatBitsPerSecond((long)(rate * 8)) : "-";

    public long ErrorCount => (_port.IfInErrorsDelta ?? 0) + (_port.IfOutErrorsDelta ?? 0);

    public bool HasErrors => ErrorCount > 0;

    /// <summary>e.g. "Ethernet", second line "fullDuplex" - matches how LibreNMS's own port page labels this.</summary>
    public string MediaText => IfTypeDisplay.Resolve(_port.IfType);

    public string? DuplexText => string.IsNullOrWhiteSpace(_port.IfDuplex) || string.Equals(_port.IfDuplex, "unknown", StringComparison.OrdinalIgnoreCase)
        ? null
        : _port.IfDuplex;

    public bool HasDuplex => DuplexText is not null;

    /// <summary>Colon-separated, since SNMP hands this back as a bare hex string.</summary>
    public string MacAddressText => FormatMacAddress(_port.IfPhysAddress);

    public string MtuText => _port.IfMtu is { } mtu && mtu > 0 ? mtu.ToString(CultureInfo.InvariantCulture) : "-";

    public RelayCommand OpenNeighborCommand { get; }

    public bool HasNeighbor => _link is not null;

    /// <summary>e.g. "r-sw-pit-10 (Gi0/1)" - the device and port this one is physically connected to, if LibreNMS has discovered one.</summary>
    public string? NeighborText => _link is null
        ? null
        : string.IsNullOrWhiteSpace(_link.RemotePort) ? _link.DisplayRemoteName : $"{_link.DisplayRemoteName} ({_link.RemotePort})";

    /// <summary>True only when the neighbour is itself a device this LibreNMS instance monitors, so there is somewhere to jump to.</summary>
    public bool CanOpenNeighbor => _link?.RemoteDeviceId is > 0;

    public bool Matches(string term) =>
        DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (_port.IfName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (_port.IfAlias?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string Capitalise(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string FormatMacAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "-";
        }

        var hex = raw.Replace(":", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty);
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
        {
            return raw;
        }

        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToLowerInvariant();
    }

    private static string FormatBitsPerSecond(long? bitsPerSecond)
    {
        if (bitsPerSecond is not { } bps || bps <= 0)
        {
            return "-";
        }

        string[] units = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        double value = bps;
        var unit = 0;

        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

/// <summary>One row in a device's currently active alerts, shown on the Overview tab.</summary>
public sealed class ActiveAlertItemViewModel
{
    private readonly Alert _alert;
    private readonly bool _serverTimestampsAreUtc;

    public ActiveAlertItemViewModel(Alert alert, bool serverTimestampsAreUtc)
    {
        _alert = alert;
        _serverTimestampsAreUtc = serverTimestampsAreUtc;
    }

    public string RuleName => _alert.DisplayRuleName;

    public AlertSeverity Severity => _alert.Severity;

    public string SeverityText => Severity.ToDisplayString();

    public AlertState State => _alert.State;

    public string StateText => State.ToDisplayString();

    private DateTime? LocalTimestamp => _alert.Timestamp is { } t
        ? (_serverTimestampsAreUtc ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime() : t)
        : null;

    public string AgeText => LocalTimestamp is { } local
        ? FormatAge(DateTime.Now - local)
        : "-";

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalDays >= 1)
        {
            return $"{(int)age.TotalDays}d {age.Hours}h";
        }

        if (age.TotalHours >= 1)
        {
            return $"{(int)age.TotalHours}h {age.Minutes}m";
        }

        return $"{Math.Max(1, (int)age.TotalMinutes)}m";
    }
}

/// <summary>One row in a device's alert history (<c>/api/v0/logs/alertlog</c>).</summary>
public sealed class AlertLogItemViewModel
{
    private readonly AlertLogEntry _entry;
    private readonly AlertRule? _rule;
    private readonly AlertDetail _detail;

    public AlertLogItemViewModel(AlertLogEntry entry, AlertRule? rule, AlertDetail detail)
    {
        _entry = entry;
        _rule = rule;
        _detail = detail;
    }

    public string TimeText => _entry.TimeLogged is { } t
        ? t.ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)
        : "-";

    public string RuleName => _rule?.Name ?? $"Rule {_entry.RuleId}";

    public AlertSeverity Severity => _rule?.Severity ?? AlertSeverity.Unknown;

    public string SeverityText => Severity == AlertSeverity.Unknown ? "-" : Severity.ToDisplayString();

    public AlertState State => _entry.State;

    public string StateText => State.ToDisplayString();

    /// <summary>
    /// What actually matched, e.g. "Gi0/0/1 - uplink to core - ifOperStatus =
    /// down", so the row says what happened without cross-referencing the
    /// Alerts tab. Empty when LibreNMS recorded no detail for this entry
    /// (older rows, or a rule with no matched-row data).
    /// </summary>
    public string DetailText
    {
        get
        {
            if (!_detail.HasFaults)
            {
                return string.Empty;
            }

            var fault = _detail.Faults[0];
            var fields = fault.PrimaryFields.Count > 0 ? fault.PrimaryFields : fault.Fields;
            var suffix = fields.Count > 0 ? " - " + string.Join(", ", fields.Select(f => $"{f.Name} = {f.Value}")) : string.Empty;
            var more = _detail.Faults.Count > 1 ? $" (+{_detail.Faults.Count - 1} more)" : string.Empty;

            return fault.Title + suffix + more;
        }
    }

    public bool HasDetail => DetailText.Length > 0;
}

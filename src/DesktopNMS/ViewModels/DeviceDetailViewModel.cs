using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
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
    Resources,

    /// <summary>Both this device's currently active alerts and its historical alert log - see <see cref="Views.DeviceView"/>.</summary>
    Alerts,
    EventLog,
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
    private readonly HashSet<int> _loadedEventLogIds = new();

    private const int EventLogPageSize = 50;
    private const int MaxOutagesShown = 10;
    private const int AvailabilityTimelineDays = 30;

    private int _eventLogLimit = EventLogPageSize;

    private double? _availability1Day;
    private double? _availability7Day;
    private double? _availability30Day;
    private double? _availability1Year;

    private Device? _device;
    private bool _isUnderMaintenance;
    private bool _isBusy;
    private string? _errorMessage;
    private DeviceDetailSection _selectedSection = DeviceDetailSection.Overview;
    private string _eventLogSearchText = string.Empty;

    // Starts false, not true: until the first page has actually loaded and
    // said so, there is nothing confirmed to load more of. Defaulting this to
    // true let a "load more" fired before that first page finished (e.g. a
    // ScrollChanged on the still-empty grid) race the initial load and see
    // every entry as a duplicate, which read as "pagination is broken" and
    // latched this false for good before the user ever got to scroll for real.
    private bool _hasMoreEventLog;
    private bool _isLoadingMoreEventLog;

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
        SensorGroups = new ObservableCollection<SensorGroupViewModel>();
        AlertHistory = new ObservableCollection<AlertLogItemViewModel>();
        ActiveAlerts = new ObservableCollection<ActiveAlertItemViewModel>();
        Ports = new ObservableCollection<PortItemViewModel>();
        Processors = new ObservableCollection<ProcessorItemViewModel>();
        Mempools = new ObservableCollection<MempoolItemViewModel>();
        Storage = new ObservableCollection<StorageItemViewModel>();
        Outages = new ObservableCollection<OutageItemViewModel>();
        AvailabilityTimeline = new ObservableCollection<OutageDayViewModel>();
        EventLog = new ObservableCollection<EventLogItemViewModel>();

        // Filter only - no grouping, so this does not run into the DataGrid
        // grouping/full-width fight the Sensors tab did.
        EventLogView = CollectionViewSource.GetDefaultView(EventLog);
        EventLogView.Filter = FilterEventLogEntry;

        ShowAlertsCommand = new RelayCommand(() => _windows.ShowAlertsForDevice(_device?.Hostname ?? Name));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _session.IsConnected && !IsBusy);

        SelectOverviewCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Overview);
        SelectSensorsCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Sensors);
        SelectPortsCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Ports);
        SelectResourcesCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Resources);
        SelectAlertsCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Alerts);
        SelectEventLogCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.EventLog);

        LoadMoreEventLogCommand = new AsyncRelayCommand(LoadMoreEventLogAsync, () => HasMoreEventLog && !IsLoadingMoreEventLog);

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
        _ = LoadResourcesAsync();
        _ = LoadAvailabilityAsync();
        _ = LoadEventLogAsync();
    }

    public ObservableCollection<SensorItemViewModel> Sensors { get; }

    /// <summary>
    /// The same sensors, bucketed for display by
    /// <see cref="SensorItemViewModel.GroupKey"/> - e.g. every reading for
    /// one transceiver, or a PSU's voltage/current/power that all share a
    /// name - so related readings read as one thing instead of scattered
    /// rows. Built explicitly rather than via an ICollectionView's grouping:
    /// a grouped DataGrid does not lay its rows out at full width without a
    /// fight, and this list needs no sorting or selection to justify one.
    /// </summary>
    public ObservableCollection<SensorGroupViewModel> SensorGroups { get; }

    public ObservableCollection<AlertLogItemViewModel> AlertHistory { get; }

    public ObservableCollection<ActiveAlertItemViewModel> ActiveAlerts { get; }

    public ObservableCollection<PortItemViewModel> Ports { get; }

    public ObservableCollection<ProcessorItemViewModel> Processors { get; }

    public ObservableCollection<MempoolItemViewModel> Mempools { get; }

    public ObservableCollection<StorageItemViewModel> Storage { get; }

    /// <summary>
    /// The device's recorded downtime incidents, newest first, capped to
    /// <see cref="MaxOutagesShown"/> - a long-lived device can accumulate a
    /// lot of these, and only the recent ones are actually useful at a glance.
    /// </summary>
    public ObservableCollection<OutageItemViewModel> Outages { get; }

    /// <summary>
    /// One entry per of the last <see cref="AvailabilityTimelineDays"/> days,
    /// oldest first - a compact status-page-style history bar, built from the
    /// same outage data as <see cref="Outages"/> rather than a second fetch.
    /// </summary>
    public ObservableCollection<OutageDayViewModel> AvailabilityTimeline { get; }

    public ObservableCollection<EventLogItemViewModel> EventLog { get; }

    /// <summary>The event log, filtered by <see cref="EventLogSearchText"/>. What the Event log tab actually binds to.</summary>
    public ICollectionView EventLogView { get; }

    public RelayCommand ShowAlertsCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand SelectOverviewCommand { get; }

    public RelayCommand SelectSensorsCommand { get; }

    public RelayCommand SelectPortsCommand { get; }

    public RelayCommand SelectResourcesCommand { get; }

    public RelayCommand SelectAlertsCommand { get; }

    public RelayCommand SelectEventLogCommand { get; }

    public AsyncRelayCommand LoadMoreEventLogCommand { get; }

    /// <summary>Free-text filter over the event log's message, type and username - applied client-side over whatever pages have been loaded so far.</summary>
    public string EventLogSearchText
    {
        get => _eventLogSearchText;
        set
        {
            if (SetProperty(ref _eventLogSearchText, value))
            {
                EventLogView.Refresh();
            }
        }
    }

    /// <summary>
    /// True while the last full page fetched actually contained new entries -
    /// once a page comes back empty, or entirely duplicates what is already
    /// loaded (a sign paging is not advancing - see <see cref="ILogsApi.ListEventLogAsync"/>),
    /// this goes false and "load more" stops firing.
    /// </summary>
    public bool HasMoreEventLog
    {
        get => _hasMoreEventLog;
        private set
        {
            if (SetProperty(ref _hasMoreEventLog, value))
            {
                LoadMoreEventLogCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoadingMoreEventLog
    {
        get => _isLoadingMoreEventLog;
        private set
        {
            if (SetProperty(ref _isLoadingMoreEventLog, value))
            {
                LoadMoreEventLogCommand.RaiseCanExecuteChanged();
            }
        }
    }

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
                OnPropertyChanged(nameof(IsResourcesSelected));
                OnPropertyChanged(nameof(IsAlertsSelected));
                OnPropertyChanged(nameof(IsEventLogSelected));
            }
        }
    }

    public bool IsOverviewSelected => SelectedSection == DeviceDetailSection.Overview;

    public bool IsSensorsSelected => SelectedSection == DeviceDetailSection.Sensors;

    public bool IsPortsSelected => SelectedSection == DeviceDetailSection.Ports;

    public bool IsResourcesSelected => SelectedSection == DeviceDetailSection.Resources;

    public bool IsAlertsSelected => SelectedSection == DeviceDetailSection.Alerts;

    public bool IsEventLogSelected => SelectedSection == DeviceDetailSection.EventLog;

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

    public bool HasEventLog => EventLog.Count > 0;

    /// <summary>
    /// True once ports have actually been fetched and the device reports at
    /// least one - many devices (UPS units, cameras, appliances) have no SNMP
    /// interfaces at all, so the Ports tab only shows up for devices that have them.
    /// </summary>
    public bool HasPorts => Ports.Count > 0;

    public int PortsUpCount => Ports.Count(p => p.IsUp);

    public int PortsDownCount => Ports.Count(p => !p.IsUp);

    /// <summary>
    /// True once CPU/memory/disk have actually been fetched and the device
    /// reports at least one of them - like <see cref="HasPorts"/>, plenty of
    /// devices (switches, PDUs, sensors-only appliances) expose none of these.
    /// </summary>
    public bool HasResources => Processors.Count > 0 || Mempools.Count > 0 || Storage.Count > 0;

    public bool HasProcessors => Processors.Count > 0;

    public bool HasMempools => Mempools.Count > 0;

    public bool HasStorage => Storage.Count > 0;

    /// <summary>Average usage across every CPU/core LibreNMS reports, or null when the device has none.</summary>
    public double? CpuUsagePercent => Processors.Count > 0 ? Processors.Average(p => p.UsagePercent) : null;

    /// <summary>
    /// The pool named "Physical memory" if there is one (the common case on
    /// Linux/UCD-SNMP hosts) - otherwise whichever pool is fullest, since an
    /// arbitrary vendor's naming cannot be relied on and the fullest pool is
    /// the one worth surfacing at a glance regardless.
    /// </summary>
    public double? MemoryUsagePercent => Mempools.Count == 0
        ? null
        : (Mempools.FirstOrDefault(m => m.Description.Contains("Physical", StringComparison.OrdinalIgnoreCase))
            ?? Mempools.OrderByDescending(m => m.UsagePercent).First()).UsagePercent;

    /// <summary>The fullest volume, since that is the one worth surfacing at a glance regardless of how many others are healthy.</summary>
    public double? DiskUsagePercent => Storage.Count > 0 ? Storage.Max(s => s.UsagePercent) : null;

    /// <summary>"24% CPU, 63% RAM, 92% disk" for the Overview card - only the metrics this device actually reports.</summary>
    public string ResourceSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (CpuUsagePercent is { } cpu) parts.Add($"{cpu:0}% CPU");
            if (MemoryUsagePercent is { } memory) parts.Add($"{memory:0}% RAM");
            if (DiskUsagePercent is { } disk) parts.Add($"{disk:0}% disk");
            return string.Join(", ", parts);
        }
    }

    /// <summary>True once availability has actually been fetched - hides the Overview card rather than showing dashes until then.</summary>
    public bool HasAvailability => _availability1Day is not null;

    public string Availability1DayText => FormatPercent(_availability1Day);

    public string Availability7DayText => FormatPercent(_availability7Day);

    public string Availability30DayText => FormatPercent(_availability30Day);

    public string Availability1YearText => FormatPercent(_availability1Year);

    public bool HasOutages => Outages.Count > 0;

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

            // dBm/signal/temperature/fan speed have an app-configured
            // fallback (see SensorCategoryRegistry) for whichever bound the
            // sensor itself leaves unconfigured, and respect the Settings
            // toggle to always prefer that fallback; every other class has
            // only the sensor's own limits to go on.
            var entry = SensorCategoryRegistry.Resolve(sensor.SensorClass);
            var evaluator = entry?.Thresholds(settings, sensor) ?? new SensorLimitThresholdEvaluator(sensor);
            var unit = entry?.UnitSuffix ?? SensorUnitDisplay.Resolve(sensor.SensorClass);

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

        RebuildSensorGroupsIfChanged();

        OnPropertyChanged(nameof(HasSensors));
        OnPropertyChanged(nameof(SensorWarningCount));
        OnPropertyChanged(nameof(SensorCriticalCount));
        OnPropertyChanged(nameof(SensorAlertSummaryText));
    }

    /// <summary>
    /// Rebuilds <see cref="SensorGroups"/>, but only when the grouping
    /// actually differs from what is already on screen. Which sensors a
    /// device has barely ever changes, so the common case - every poll - is a
    /// no-op, and the rows keep their view models and update their values in
    /// place rather than the whole list being torn down and rebuilt (which
    /// would lose the scroll position every 30 seconds).
    /// </summary>
    private void RebuildSensorGroupsIfChanged()
    {
        var grouped = Sensors
            .GroupBy(s => s.GroupKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SensorGroupViewModel(
                g.Key,
                g.OrderBy(s => s.Description, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        if (MatchesCurrentGroups(grouped))
        {
            return;
        }

        SensorGroups.Clear();
        foreach (var group in grouped)
        {
            SensorGroups.Add(group);
        }
    }

    private bool MatchesCurrentGroups(IReadOnlyList<SensorGroupViewModel> candidate)
    {
        if (candidate.Count != SensorGroups.Count)
        {
            return false;
        }

        for (var i = 0; i < candidate.Count; i++)
        {
            var left = candidate[i];
            var right = SensorGroups[i];

            if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
                || left.Sensors.Count != right.Sensors.Count)
            {
                return false;
            }

            for (var j = 0; j < left.Sensors.Count; j++)
            {
                if (left.Sensors[j].SensorId != right.Sensors[j].SensorId)
                {
                    return false;
                }
            }
        }

        return true;
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

    /// <summary>
    /// CPU/memory/disk usage. Fetched independently, same as ports/neighbours,
    /// so a problem here cannot take another tab down with it - most devices
    /// (switches, PDUs, anything SNMP-only) report none of these at all, which
    /// is not an error, just an empty result.
    /// </summary>
    private async Task LoadResourcesAsync()
    {
        try
        {
            var processorsTask = _client.Health.ListProcessorsAsync(_deviceId);
            var mempoolsTask = _client.Health.ListMempoolsAsync(_deviceId);
            var storageTask = _client.Health.ListStorageAsync(_deviceId);
            await Task.WhenAll(processorsTask, mempoolsTask, storageTask).ConfigureAwait(true);

            Processors.Clear();
            foreach (var processor in processorsTask.Result.OrderBy(p => p.Description, StringComparer.OrdinalIgnoreCase))
            {
                Processors.Add(new ProcessorItemViewModel(processor));
            }

            Mempools.Clear();
            foreach (var mempool in mempoolsTask.Result.OrderBy(m => m.Description, StringComparer.OrdinalIgnoreCase))
            {
                Mempools.Add(new MempoolItemViewModel(mempool));
            }

            Storage.Clear();
            foreach (var volume in storageTask.Result.OrderBy(s => s.Description, StringComparer.OrdinalIgnoreCase))
            {
                Storage.Add(new StorageItemViewModel(volume));
            }

            OnPropertyChanged(nameof(HasResources));
            OnPropertyChanged(nameof(HasProcessors));
            OnPropertyChanged(nameof(HasMempools));
            OnPropertyChanged(nameof(HasStorage));
            OnPropertyChanged(nameof(CpuUsagePercent));
            OnPropertyChanged(nameof(MemoryUsagePercent));
            OnPropertyChanged(nameof(DiskUsagePercent));
            OnPropertyChanged(nameof(ResourceSummaryText));
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load resources for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load resources for device {DeviceId}", _deviceId);
        }
    }

    /// <summary>
    /// Uptime percentage (1 day/7 day/30 day/1 year) and downtime history.
    /// Fetched independently, same as ports/resources, so a problem here
    /// cannot take another tab down with it.
    /// </summary>
    private async Task LoadAvailabilityAsync()
    {
        try
        {
            var availabilityTask = _client.Devices.GetAvailabilityAsync(_deviceId);
            var outagesTask = _client.Devices.GetOutagesAsync(_deviceId);
            await Task.WhenAll(availabilityTask, outagesTask).ConfigureAwait(true);

            // Identified by duration rather than array position - LibreNMS's
            // own ordering is not worth trusting blindly, and this is cheap
            // either way since there are only ever four of them.
            double? PercentFor(long durationSeconds) => availabilityTask.Result
                .FirstOrDefault(w => w.DurationSeconds == durationSeconds)?.Percent;

            _availability1Day = PercentFor(86_400);
            _availability7Day = PercentFor(604_800);
            _availability30Day = PercentFor(2_592_000);
            _availability1Year = PercentFor(31_536_000);

            Outages.Clear();
            foreach (var outage in outagesTask.Result
                .OrderByDescending(o => o.GoingDown)
                .Take(MaxOutagesShown))
            {
                Outages.Add(new OutageItemViewModel(outage));
            }

            AvailabilityTimeline.Clear();
            foreach (var day in BuildAvailabilityTimeline(outagesTask.Result))
            {
                AvailabilityTimeline.Add(day);
            }

            OnPropertyChanged(nameof(HasAvailability));
            OnPropertyChanged(nameof(Availability1DayText));
            OnPropertyChanged(nameof(Availability7DayText));
            OnPropertyChanged(nameof(Availability30DayText));
            OnPropertyChanged(nameof(Availability1YearText));
            OnPropertyChanged(nameof(HasOutages));
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load availability for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load availability for device {DeviceId}", _deviceId);
        }
    }

    private static string FormatPercent(double? percent) =>
        percent is { } value ? value.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "-";

    /// <summary>
    /// Buckets every outage into local calendar days over the trailing
    /// <see cref="AvailabilityTimelineDays"/> window, oldest first, summing
    /// however much of each day fell inside a down period (an outage can span
    /// midnight, or several days). An outage still ongoing (<see cref="DeviceOutage.UpAgain"/>
    /// null) is treated as down through to now.
    /// </summary>
    private static List<OutageDayViewModel> BuildAvailabilityTimeline(IReadOnlyList<DeviceOutage> outages)
    {
        var today = DateTime.Today;
        var days = new List<OutageDayViewModel>(AvailabilityTimelineDays);

        for (var offset = AvailabilityTimelineDays - 1; offset >= 0; offset--)
        {
            var dayStart = today.AddDays(-offset);
            var dayEnd = dayStart.AddDays(1);
            double downSeconds = 0;

            foreach (var outage in outages)
            {
                if (outage.GoingDown is not { } start)
                {
                    continue;
                }

                var localStart = start.ToLocalTime();
                var localEnd = (outage.UpAgain ?? DateTime.UtcNow).ToLocalTime();

                var overlapStart = localStart > dayStart ? localStart : dayStart;
                var overlapEnd = localEnd < dayEnd ? localEnd : dayEnd;

                if (overlapEnd > overlapStart)
                {
                    downSeconds += (overlapEnd - overlapStart).TotalSeconds;
                }
            }

            days.Add(new OutageDayViewModel(DateOnly.FromDateTime(dayStart), downSeconds));
        }

        return days;
    }

    /// <summary>
    /// LibreNMS's general audit trail for the device (config changes, up/down
    /// transitions, polling events, ...) - distinct from the alert log, which
    /// is only what tripped an alert rule. Fetched independently, same as
    /// ports/neighbours, so a problem here cannot take another tab down with it.
    /// Always fetches from scratch at <see cref="EventLogPageSize"/> - see
    /// <see cref="LoadMoreEventLogAsync"/> for how "load more" widens that.
    /// </summary>
    private async Task LoadEventLogAsync()
    {
        _eventLogLimit = EventLogPageSize;

        try
        {
            var entries = await _client.Logs.ListEventLogAsync(_deviceId, _eventLogLimit).ConfigureAwait(true);

            EventLog.Clear();
            _loadedEventLogIds.Clear();

            foreach (var entry in entries)
            {
                EventLog.Add(new EventLogItemViewModel(entry));
                _loadedEventLogIds.Add(entry.Id);
            }

            HasMoreEventLog = entries.Count >= _eventLogLimit;

            OnPropertyChanged(nameof(HasEventLog));
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load event log for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load event log for device {DeviceId}", _deviceId);
        }
    }

    /// <summary>
    /// LibreNMS's eventlog endpoint has no working offset parameter - a
    /// "start" query parameter was tried and confirmed (against a real
    /// server, via logging) to have no effect, always returning the same top
    /// entries regardless. "Load more" therefore re-issues the same query
    /// with a bigger <see cref="_eventLogLimit"/> - the server always returns
    /// the newest N, so a bigger N is the existing entries plus more older
    /// ones tacked on the end - and only the new tail (by id, in case that
    /// assumption ever breaks) is appended, so the grid does not visibly
    /// rebuild from scratch.
    /// </summary>
    private async Task LoadMoreEventLogAsync()
    {
        IsLoadingMoreEventLog = true;
        var newLimit = _eventLogLimit + EventLogPageSize;

        try
        {
            var entries = await _client.Logs.ListEventLogAsync(_deviceId, newLimit).ConfigureAwait(true);

            var added = 0;
            foreach (var entry in entries)
            {
                if (_loadedEventLogIds.Add(entry.Id))
                {
                    EventLog.Add(new EventLogItemViewModel(entry));
                    added++;
                }
            }

            _eventLogLimit = newLimit;
            HasMoreEventLog = added > 0 && entries.Count >= newLimit;
            OnPropertyChanged(nameof(HasEventLog));
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load more event log entries for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
            HasMoreEventLog = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load more event log entries for device {DeviceId}", _deviceId);
            HasMoreEventLog = false;
        }
        finally
        {
            IsLoadingMoreEventLog = false;
        }
    }

    private bool FilterEventLogEntry(object item)
    {
        if (item is not EventLogItemViewModel entry)
        {
            return false;
        }

        var term = EventLogSearchText;
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        return entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.TypeText.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.Username.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private Task RefreshAsync()
    {
        _deviceMonitor.RequestRefresh();
        _sensorMonitor.RequestRefresh();
        _alertMonitor.RequestRefresh();
        return Task.WhenAll(LoadAlertHistoryAsync(), LoadPortsAsync(), LoadResourcesAsync(), LoadAvailabilityAsync(), LoadEventLogAsync());
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
        OnPropertyChanged(nameof(HasLoaded));
        OnPropertyChanged(nameof(IsLoadingDevice));
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

    /// <summary>
    /// Classifies a reading purely against the limits LibreNMS itself has
    /// configured on that specific sensor (sensor_limit/_warn/_low/_low_warn) -
    /// the fallback for every sensor class outside <see cref="SensorCategoryRegistry"/>
    /// (voltage, current, power, ...), which have no app-wide setting to fall
    /// back to at all, unlike dBm/signal/temperature/fan speed (see
    /// <see cref="HybridThresholdEvaluator"/>, used for those via the registry).
    /// </summary>
    private sealed class SensorLimitThresholdEvaluator : IThresholdEvaluator
    {
        private readonly Sensor _sensor;

        public SensorLimitThresholdEvaluator(Sensor sensor) => _sensor = sensor;

        public AlertSeverity Evaluate(double value)
        {
            if (_sensor.LimitLow is null && _sensor.LimitLowWarn is null
                && _sensor.LimitHigh is null && _sensor.LimitHighWarn is null)
            {
                // Nothing configured on this sensor to judge it against -
                // "Unknown" reads as neutral, not as if it were fine.
                return AlertSeverity.Unknown;
            }

            if (_sensor.LimitLow is { } low && value <= low)
            {
                return AlertSeverity.Critical;
            }

            if (_sensor.LimitHigh is { } high && value >= high)
            {
                return AlertSeverity.Critical;
            }

            if (_sensor.LimitLowWarn is { } lowWarn && value <= lowWarn)
            {
                return AlertSeverity.Warning;
            }

            if (_sensor.LimitHighWarn is { } highWarn && value >= highWarn)
            {
                return AlertSeverity.Warning;
            }

            return AlertSeverity.Ok;
        }
    }
}

/// <summary>
/// Units for LibreNMS sensor classes outside <see cref="SensorCategoryRegistry"/>
/// (which already carries a unit for its four) - the API does not return a
/// unit string, so only classes worth labelling with confidence are included;
/// anything else (state, count, runtime, ...) is left bare rather than guessed.
/// </summary>
file static class SensorUnitDisplay
{
    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["voltage"] = " V",
        ["current"] = " A",
        ["power"] = " W",
        ["frequency"] = " Hz",
        ["humidity"] = "%",
        ["storage"] = "%",
        ["charge"] = "%",
        ["load"] = "%",
    };

    public static string Resolve(string? sensorClass) =>
        sensorClass is not null && Units.TryGetValue(sensorClass, out var unit) ? unit : string.Empty;
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

/// <summary>One group of related sensor readings on a device's Sensors tab.</summary>
public sealed class SensorGroupViewModel
{
    public SensorGroupViewModel(string name, IReadOnlyList<SensorItemViewModel> sensors)
    {
        Name = name;
        Sensors = sensors;
    }

    public string Name { get; }

    public IReadOnlyList<SensorItemViewModel> Sensors { get; }

    public int Count => Sensors.Count;

    /// <summary>The worst severity in the group, so a header says at a glance whether anything inside needs attention.</summary>
    public AlertSeverity WorstSeverity => Sensors.Count == 0
        ? AlertSeverity.Unknown
        : Sensors.OrderByDescending(s => s.Severity.SortRank()).First().Severity;
}

/// <summary>One row in a device's event log - a general audit entry, not necessarily tied to any alert.</summary>
public sealed class EventLogItemViewModel
{
    private readonly EventLogEntry _entry;

    public EventLogItemViewModel(EventLogEntry entry) => _entry = entry;

    public string Message => string.IsNullOrWhiteSpace(_entry.Message) ? "-" : _entry.Message!;

    public string TypeText => string.IsNullOrWhiteSpace(_entry.Type) ? "-" : _entry.Type!;

    public string Username => string.IsNullOrWhiteSpace(_entry.Username) ? "-" : _entry.Username!;

    public string TimeText => _entry.Timestamp is { } t
        ? t.ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)
        : "-";
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

/// <summary>One row in a device's Resources tab - one CPU/core.</summary>
public sealed class ProcessorItemViewModel
{
    private readonly ProcessorSensor _processor;

    public ProcessorItemViewModel(ProcessorSensor processor) => _processor = processor;

    public string Description => string.IsNullOrWhiteSpace(_processor.Description) ? "-" : _processor.Description!;

    public double UsagePercent => _processor.UsagePercent ?? 0;

    public string UsageText => _processor.UsagePercent is { } percent ? $"{percent:0.#}%" : "-";

    public AlertSeverity Severity => ResourceSeverity.Evaluate(_processor.UsagePercent, _processor.WarningPercent);
}

/// <summary>One row in a device's Resources tab - one memory pool.</summary>
public sealed class MempoolItemViewModel
{
    private readonly MempoolSensor _mempool;

    public MempoolItemViewModel(MempoolSensor mempool) => _mempool = mempool;

    public string Description => string.IsNullOrWhiteSpace(_mempool.Description) ? "-" : _mempool.Description!;

    public double UsagePercent => _mempool.UsagePercent ?? 0;

    public string UsageText => _mempool.UsagePercent is { } percent ? $"{percent:0.#}%" : "-";

    public string DetailText => ResourceByteFormat.FormatUsedOfTotal(_mempool.UsedBytes, _mempool.TotalBytes);

    public AlertSeverity Severity => ResourceSeverity.Evaluate(_mempool.UsagePercent, _mempool.WarningPercent);
}

/// <summary>One row in a device's Resources tab - one disk/filesystem.</summary>
public sealed class StorageItemViewModel
{
    private readonly StorageVolume _volume;

    public StorageItemViewModel(StorageVolume volume) => _volume = volume;

    public string Description => string.IsNullOrWhiteSpace(_volume.Description) ? "-" : _volume.Description!;

    public double UsagePercent => _volume.UsagePercent ?? 0;

    public string UsageText => _volume.UsagePercent is { } percent ? $"{percent:0.#}%" : "-";

    public string DetailText => ResourceByteFormat.FormatUsedOfTotal(_volume.UsedBytes, _volume.TotalBytes);

    public AlertSeverity Severity => ResourceSeverity.Evaluate(_volume.UsagePercent, _volume.WarningPercent);
}

/// <summary>One row in a device's Overview "outages" list - a period the device was recorded down.</summary>
public sealed class OutageItemViewModel
{
    private readonly DeviceOutage _outage;

    public OutageItemViewModel(DeviceOutage outage) => _outage = outage;

    public string StartedText => _outage.GoingDown is { } t
        ? t.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
        : "-";

    public string EndedText => _outage.UpAgain is { } t
        ? t.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
        : "ongoing";

    public string DurationText => _outage is { GoingDown: { } start, UpAgain: { } end }
        ? DurationFormat.Format(end - start)
        : "-";
}

/// <summary>
/// One day in the Overview "availability timeline" bar - a compact, at-a-glance
/// history strip (like a status page's uptime bar) rather than the individual
/// incident list <see cref="OutageItemViewModel"/> covers. Deliberately binary
/// (a day either had downtime or it did not) rather than graded by how much,
/// since LibreNMS itself does not track a meaningful "partial" threshold here -
/// the exact amount is still available in <see cref="TooltipText"/>.
/// </summary>
public sealed class OutageDayViewModel
{
    public OutageDayViewModel(DateOnly date, double downSeconds)
    {
        Date = date;
        DownSeconds = downSeconds;
    }

    public DateOnly Date { get; }

    public double DownSeconds { get; }

    public bool HadOutage => DownSeconds > 0;

    public AlertSeverity Severity => HadOutage ? AlertSeverity.Critical : AlertSeverity.Ok;

    public string TooltipText => HadOutage
        ? $"{Date:dd MMM}: down {DurationFormat.Format(TimeSpan.FromSeconds(DownSeconds))}"
        : $"{Date:dd MMM}: no downtime";
}

file static class DurationFormat
{
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

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
}

/// <summary>
/// Shared severity rule for CPU/memory/disk rows. Unlike <see cref="Sensor"/>,
/// LibreNMS only tracks one boundary for these (a single "warning" percent,
/// no separate critical) - so a configured boundary reads as Warning, and a
/// device left unconfigured falls back to fixed, generic bands. Either way,
/// a reading at or past 95% is always Critical: however it is configured,
/// that is no longer a warning.
/// </summary>
file static class ResourceSeverity
{
    private const double AlwaysCriticalAt = 95;
    private const double DefaultWarningAt = 90;

    public static AlertSeverity Evaluate(double? usagePercent, double? warningPercent)
    {
        if (usagePercent is not { } percent)
        {
            return AlertSeverity.Unknown;
        }

        if (percent >= AlwaysCriticalAt)
        {
            return AlertSeverity.Critical;
        }

        var warning = warningPercent ?? DefaultWarningAt;
        return percent >= warning ? AlertSeverity.Warning : AlertSeverity.Ok;
    }
}

/// <summary>Human-readable "12.3 GB / 64 GB" for a memory pool or disk volume.</summary>
file static class ResourceByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string FormatUsedOfTotal(long? usedBytes, long? totalBytes)
    {
        if (usedBytes is not { } used || totalBytes is not { } total || total <= 0)
        {
            return "-";
        }

        return $"{Format(used)} / {Format(total)}";
    }

    private static string Format(long bytes)
    {
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}

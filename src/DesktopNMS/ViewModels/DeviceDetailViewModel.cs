using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
    private readonly ILibreNmsClient _client;
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

    public DeviceDetailViewModel(
        int deviceId,
        DeviceMonitor deviceMonitor,
        SensorMonitor sensorMonitor,
        IDeviceCache deviceCache,
        ILibreNmsClient client,
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        ILogger<DeviceDetailViewModel> logger)
    {
        _deviceId = deviceId;
        _deviceMonitor = deviceMonitor;
        _sensorMonitor = sensorMonitor;
        _client = client;
        _session = session;
        _settings = settings;
        _windows = windows;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Sensors = new ObservableCollection<SensorItemViewModel>();
        AlertHistory = new ObservableCollection<AlertLogItemViewModel>();

        OpenInLibreNmsCommand = new RelayCommand(() =>
        {
            if (DeviceUrl is { } url)
            {
                _windows.OpenUrl(url);
            }
        }, () => DeviceUrl is not null);

        ShowAlertsCommand = new RelayCommand(() => _windows.ShowAlertsForDevice(_device?.Hostname ?? Name));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _session.IsConnected && !IsBusy);

        // Shows whatever is already cached instantly, rather than a blank
        // window until the next shared poll lands.
        _device = deviceCache.Get(deviceId);

        _deviceMonitor.Polled += OnDevicePolled;
        _sensorMonitor.Polled += OnSensorPolled;

        // Both monitors are almost certainly already running - the Devices tab
        // that opened this window depends on DeviceMonitor - but Start() is
        // idempotent, and a Sensors widget being the only prior consumer of
        // SensorMonitor should not leave this window's sensor list empty.
        _deviceMonitor.Start();
        _sensorMonitor.Start();
        _deviceMonitor.RequestRefresh();
        _sensorMonitor.RequestRefresh();

        _ = LoadAlertHistoryAsync();
    }

    public ObservableCollection<SensorItemViewModel> Sensors { get; }

    public ObservableCollection<AlertLogItemViewModel> AlertHistory { get; }

    public RelayCommand OpenInLibreNmsCommand { get; }

    public RelayCommand ShowAlertsCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

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

    public bool HasSensors => Sensors.Count > 0;

    public bool HasAlertHistory => AlertHistory.Count > 0;

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
            var ruleIds = entries.Select(e => e.RuleId).Distinct();

            await Task.WhenAll(ruleIds.Select(EnsureRuleCachedAsync)).ConfigureAwait(true);

            AlertHistory.Clear();
            foreach (var entry in entries)
            {
                _ruleCache.TryGetValue(entry.RuleId, out var rule);
                AlertHistory.Add(new AlertLogItemViewModel(entry, rule));
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

    private Task RefreshAsync()
    {
        _deviceMonitor.RequestRefresh();
        _sensorMonitor.RequestRefresh();
        return LoadAlertHistoryAsync();
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
    }

    private sealed class NoThresholds : IThresholdEvaluator
    {
        public static readonly NoThresholds Instance = new();

        public AlertSeverity Evaluate(double value) => AlertSeverity.Unknown;
    }
}

/// <summary>One row in a device's alert history (<c>/api/v0/logs/alertlog</c>).</summary>
public sealed class AlertLogItemViewModel
{
    private readonly AlertLogEntry _entry;
    private readonly AlertRule? _rule;

    public AlertLogItemViewModel(AlertLogEntry entry, AlertRule? rule)
    {
        _entry = entry;
        _rule = rule;
    }

    public string TimeText => _entry.TimeLogged is { } t
        ? t.ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)
        : "-";

    public string RuleName => _rule?.Name ?? $"Rule {_entry.RuleId}";

    public AlertSeverity Severity => _rule?.Severity ?? AlertSeverity.Unknown;

    public string SeverityText => Severity == AlertSeverity.Unknown ? "-" : Severity.ToDisplayString();

    public AlertState State => _entry.State;

    public string StateText => State.ToDisplayString();
}

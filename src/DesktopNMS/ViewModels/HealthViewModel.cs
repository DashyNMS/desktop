using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>View model behind the Health tab: sensor readings against configurable thresholds.</summary>
public sealed class HealthViewModel : ObservableObject, IDisposable
{
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly IWindowService _windows;
    private readonly ILogger<HealthViewModel> _logger;
    private readonly Dictionary<int, SensorItemViewModel> _index = new();

    private CancellationTokenSource? _loadCts;
    private SensorItemViewModel? _selectedSensor;
    private string _statusMessage = "Not loaded yet.";
    private string? _errorMessage;
    private bool _isBusy;
    private DateTimeOffset? _lastUpdated;
    private string _searchText = string.Empty;
    private bool _hasLoadedOnce;

    private bool _showCritical = true;
    private bool _showWarning = true;
    private bool _showOk = true;
    private bool _showUnknown = true;

    public HealthViewModel(
        ILibreNmsClient client,
        ISessionService session,
        ISettingsStore settings,
        IDeviceCache devices,
        IWindowService windows,
        ILogger<HealthViewModel> logger)
    {
        _client = client;
        _session = session;
        _settings = settings;
        _devices = devices;
        _windows = windows;
        _logger = logger;

        Sensors = new ObservableCollection<SensorItemViewModel>();
        SensorsView = CollectionViewSource.GetDefaultView(Sensors);
        SensorsView.Filter = FilterSensor;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _session.IsConnected && !IsBusy);
        OpenDeviceCommand = new RelayCommand(OpenSelectedDevice, () => SelectedSensor?.DeviceUrl is not null);
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        _settings.Changed += OnSettingsChanged;
    }

    public ObservableCollection<SensorItemViewModel> Sensors { get; }

    public ICollectionView SensorsView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    // -------------------------------------------------------------- filtering

    public bool ShowCritical
    {
        get => _showCritical;
        set { if (SetProperty(ref _showCritical, value)) OnFilterChanged(); }
    }

    public bool ShowWarning
    {
        get => _showWarning;
        set { if (SetProperty(ref _showWarning, value)) OnFilterChanged(); }
    }

    public bool ShowOk
    {
        get => _showOk;
        set { if (SetProperty(ref _showOk, value)) OnFilterChanged(); }
    }

    /// <summary>Readings at or above the "ignore" sentinel - typically an unplugged port.</summary>
    public bool ShowUnknown
    {
        get => _showUnknown;
        set { if (SetProperty(ref _showUnknown, value)) OnFilterChanged(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) OnFilterChanged(); }
    }

    // ------------------------------------------------------------------ state

    public SensorItemViewModel? SelectedSensor
    {
        get => _selectedSensor;
        set
        {
            if (SetProperty(ref _selectedSensor, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OpenDeviceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => SelectedSensor is not null;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
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

    public string LastUpdatedText => _lastUpdated is null
        ? "never"
        : _lastUpdated.Value.LocalDateTime.ToString("HH:mm:ss");

    public int CriticalCount => Sensors.Count(s => s.Severity == AlertSeverity.Critical);

    public int WarningCount => Sensors.Count(s => s.Severity == AlertSeverity.Warning);

    public int OkCount => Sensors.Count(s => s.Severity == AlertSeverity.Ok);

    public int TotalCount => Sensors.Count;

    public int VisibleCount => SensorsView.Cast<object>().Count();

    // --------------------------------------------------------------- lifetime

    /// <summary>Called each time the tab is shown; loads once, then leaves it to manual refresh.</summary>
    public void OnShown()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!_session.IsConnected)
        {
            StatusMessage = "Not connected.";
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var sensors = await _client.Sensors.ListAsync(token).ConfigureAwait(true);
            var dbmSensors = sensors.Where(s => s.IsDbm).ToList();

            await _devices.EnsureCurrentAsync(dbmSensors.Select(s => s.DeviceId), token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            ApplySensors(dbmSensors);

            _hasLoadedOnce = true;
            _lastUpdated = DateTimeOffset.Now;

            StatusMessage = TotalCount == 0
                ? "No dBm sensors found."
                : $"{CriticalCount} critical, {WarningCount} warning, {OkCount} ok, {TotalCount} total.";
            OnPropertyChanged(nameof(LastUpdatedText));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load sensor health");
            ErrorMessage = ex.ToUserMessage();
            StatusMessage = "Last refresh failed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load sensor health");
            ErrorMessage = ex.Message;
            StatusMessage = "Last refresh failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySensors(IReadOnlyList<Sensor> sensors)
    {
        var connection = _session.Connection;
        var thresholds = _settings.Current.DbmThresholds;

        var ordered = sensors
            .OrderBy(s => DeviceNameFor(s.DeviceId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var incoming = ordered.Select(s => s.SensorId).ToHashSet();

        for (var i = Sensors.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Sensors[i].SensorId))
            {
                _index.Remove(Sensors[i].SensorId);
                Sensors.RemoveAt(i);
            }
        }

        for (var target = 0; target < ordered.Count; target++)
        {
            var sensor = ordered[target];
            var deviceName = DeviceNameFor(sensor.DeviceId);

            if (_index.TryGetValue(sensor.SensorId, out var existing))
            {
                existing.Update(sensor, deviceName, connection, thresholds);

                var currentIndex = Sensors.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != target && target < Sensors.Count)
                {
                    Sensors.Move(currentIndex, target);
                }
            }
            else
            {
                var item = new SensorItemViewModel(sensor, deviceName, connection, thresholds);
                _index[sensor.SensorId] = item;
                Sensors.Insert(Math.Min(target, Sensors.Count), item);
            }
        }

        RaiseCountsChanged();

        if (SelectedSensor is not null && !_index.ContainsKey(SelectedSensor.SensorId))
        {
            SelectedSensor = null;
        }
    }

    private string DeviceNameFor(int deviceId) => _devices.Get(deviceId)?.BestName ?? $"device {deviceId}";

    // --------------------------------------------------------------- commands

    private void OpenSelectedDevice()
    {
        if (SelectedSensor?.DeviceUrl is { } url)
        {
            _windows.OpenUrl(url);
        }
    }

    private void ClearFilters()
    {
        ShowCritical = true;
        ShowWarning = true;
        ShowOk = true;
        ShowUnknown = true;
        SearchText = string.Empty;
    }

    // ---------------------------------------------------------------- helpers

    private bool FilterSensor(object item)
    {
        if (item is not SensorItemViewModel sensor)
        {
            return false;
        }

        var severityAllowed = sensor.Severity switch
        {
            AlertSeverity.Critical => ShowCritical,
            AlertSeverity.Warning => ShowWarning,
            AlertSeverity.Ok => ShowOk,
            _ => ShowUnknown,
        };

        if (!severityAllowed)
        {
            return false;
        }

        var term = SearchText;
        return string.IsNullOrWhiteSpace(term) || sensor.Matches(term.Trim());
    }

    private void OnFilterChanged()
    {
        SensorsView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var thresholds = settings.DbmThresholds;

        foreach (var sensor in Sensors)
        {
            sensor.ApplyThresholds(thresholds);
        }

        RaiseCountsChanged();
    }

    private void RaiseCountsChanged()
    {
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(OkCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));

        // A threshold change can move rows in or out of the current filter
        // without the ObservableCollection itself changing.
        SensorsView.Refresh();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}

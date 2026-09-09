using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>Which sub-tab of the Health tab is showing.</summary>
public enum HealthCategory
{
    Dbm,
    Signal,
    Temperature,
    FanSpeed,
}

/// <summary>
/// View model behind the Health tab. Fetches every sensor across the fleet in
/// one call (there is no server-side filter, see <see cref="ISensorsApi"/>)
/// and hands each category (dBm, signal, temperature, fan speed) its slice of
/// the results - one API call regardless of how many categories exist.
/// </summary>
public sealed class HealthViewModel : ObservableObject, IDisposable
{
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly ILogger<HealthViewModel> _logger;

    private CancellationTokenSource? _loadCts;
    private string _statusMessage = "Not loaded yet.";
    private string? _errorMessage;
    private bool _isBusy;
    private DateTimeOffset? _lastUpdated;
    private bool _hasLoadedOnce;
    private HealthCategory _selectedCategory = HealthCategory.Dbm;
    private readonly AutoRefreshTimer _autoRefresh;

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
        _logger = logger;

        Dbm = new SensorCategoryViewModel(windows, s => s.DbmThresholds, " dBm", "No dBm sensors found.");
        Signal = new SensorCategoryViewModel(windows, s => s.SignalThresholds, string.Empty, "No signal sensors found.");
        Temperature = new SensorCategoryViewModel(windows, s => s.TemperatureThresholds, " °C", "No temperature sensors found.");
        FanSpeed = new SensorCategoryViewModel(windows, s => s.FanSpeedThresholds, " RPM", "No fan speed sensors found.");

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _session.IsConnected && !IsBusy);
        ClearFiltersCommand = new RelayCommand(() => CurrentCategory.ClearFiltersCommand.Execute(null));

        SelectDbmCategoryCommand = new RelayCommand(() => SelectedCategory = HealthCategory.Dbm);
        SelectSignalCategoryCommand = new RelayCommand(() => SelectedCategory = HealthCategory.Signal);
        SelectTemperatureCategoryCommand = new RelayCommand(() => SelectedCategory = HealthCategory.Temperature);
        SelectFanSpeedCategoryCommand = new RelayCommand(() => SelectedCategory = HealthCategory.FanSpeed);

        _autoRefresh = new AutoRefreshTimer(() => _settings.Current.PollIntervalSeconds, () => _ = RefreshAsync());
        _autoRefresh.RemainingChanged += (_, _) => OnPropertyChanged(nameof(NextRefreshText));

        Dbm.PropertyChanged += OnCategoryPropertyChanged;
        Signal.PropertyChanged += OnCategoryPropertyChanged;
        Temperature.PropertyChanged += OnCategoryPropertyChanged;
        FanSpeed.PropertyChanged += OnCategoryPropertyChanged;

        _settings.Changed += OnSettingsChanged;
    }

    public SensorCategoryViewModel Dbm { get; }

    public SensorCategoryViewModel Signal { get; }

    public SensorCategoryViewModel Temperature { get; }

    public SensorCategoryViewModel FanSpeed { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>A short "45s" / "2:05" countdown to the next automatic refresh.</summary>
    public string NextRefreshText => _autoRefresh.RemainingText;

    /// <summary>Clears the filters on whichever category sub-tab is currently showing.</summary>
    public RelayCommand ClearFiltersCommand { get; }

    public RelayCommand SelectDbmCategoryCommand { get; }

    public RelayCommand SelectSignalCategoryCommand { get; }

    public RelayCommand SelectTemperatureCategoryCommand { get; }

    public RelayCommand SelectFanSpeedCategoryCommand { get; }

    public HealthCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(IsDbmCategorySelected));
                OnPropertyChanged(nameof(IsSignalCategorySelected));
                OnPropertyChanged(nameof(IsTemperatureCategorySelected));
                OnPropertyChanged(nameof(IsFanSpeedCategorySelected));
                OnPropertyChanged(nameof(VisibleCount));
            }
        }
    }

    /// <summary>How many rows the currently showing category's filters leave visible.</summary>
    public int VisibleCount => CurrentCategory.VisibleCount;

    public bool IsDbmCategorySelected => SelectedCategory == HealthCategory.Dbm;

    public bool IsSignalCategorySelected => SelectedCategory == HealthCategory.Signal;

    public bool IsTemperatureCategorySelected => SelectedCategory == HealthCategory.Temperature;

    public bool IsFanSpeedCategorySelected => SelectedCategory == HealthCategory.FanSpeed;

    private SensorCategoryViewModel CurrentCategory => SelectedCategory switch
    {
        HealthCategory.Signal => Signal,
        HealthCategory.Temperature => Temperature,
        HealthCategory.FanSpeed => FanSpeed,
        _ => Dbm,
    };

    // ------------------------------------------------------------------ state

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

    // --------------------------------------------------------------- lifetime

    /// <summary>
    /// Called each time the tab is shown; loads once, then keeps refreshing
    /// automatically on the polling interval from Settings (same as Alerts).
    /// </summary>
    public void OnShown()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _ = RefreshAsync();
        _autoRefresh.Start();
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

            var dbmSensors = sensors.Where(s => s.HasClass("dbm")).ToList();
            var signalSensors = sensors.Where(s => s.HasClass("signal")).ToList();
            var temperatureSensors = sensors.Where(s => s.HasClass("temperature")).ToList();
            var fanSpeedSensors = sensors.Where(s => s.HasClass("fanspeed")).ToList();

            var relevantDeviceIds = dbmSensors
                .Concat(signalSensors)
                .Concat(temperatureSensors)
                .Concat(fanSpeedSensors)
                .Select(s => s.DeviceId)
                .Distinct();

            await _devices.EnsureCurrentAsync(relevantDeviceIds, token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            var connection = _session.Connection;
            var settings = _settings.Current;
            string DeviceNameFor(int deviceId) => _devices.Get(deviceId)?.BestName ?? $"device {deviceId}";

            Dbm.Apply(dbmSensors, DeviceNameFor, connection, settings);
            Signal.Apply(signalSensors, DeviceNameFor, connection, settings);
            Temperature.Apply(temperatureSensors, DeviceNameFor, connection, settings);
            FanSpeed.Apply(fanSpeedSensors, DeviceNameFor, connection, settings);

            _hasLoadedOnce = true;
            _lastUpdated = DateTimeOffset.Now;

            StatusMessage = $"{Dbm.TotalCount} dBm, {Signal.TotalCount} signal, {Temperature.TotalCount} temperature, {FanSpeed.TotalCount} fan speed.";
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
            _autoRefresh.Reset();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        Dbm.ApplyThresholds(settings);
        Signal.ApplyThresholds(settings);
        Temperature.ApplyThresholds(settings);
        FanSpeed.ApplyThresholds(settings);
    }

    /// <summary>Forwards the currently-selected category's VisibleCount so the shared status bar can show it.</summary>
    private void OnCategoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SensorCategoryViewModel.VisibleCount) && ReferenceEquals(sender, CurrentCategory))
        {
            OnPropertyChanged(nameof(VisibleCount));
        }
    }

    public void Dispose()
    {
        _autoRefresh.Dispose();
        Dbm.PropertyChanged -= OnCategoryPropertyChanged;
        Signal.PropertyChanged -= OnCategoryPropertyChanged;
        Temperature.PropertyChanged -= OnCategoryPropertyChanged;
        FanSpeed.PropertyChanged -= OnCategoryPropertyChanged;
        _settings.Changed -= OnSettingsChanged;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}

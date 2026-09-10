using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

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
/// View model behind the Health tab. Every sensor across the fleet comes from
/// the shared <see cref="SensorMonitor"/> (also used by the Dashboard's
/// Sensors widgets), so this tab being open never costs its own extra poll;
/// each category (dBm, signal, temperature, fan speed) gets its slice of
/// whatever the monitor last fetched.
/// </summary>
public sealed class HealthViewModel : ObservableObject, IDisposable
{
    private readonly SensorMonitor _sensorMonitor;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly Dispatcher _dispatcher;

    private string _statusMessage = "Not loaded yet.";
    private string? _errorMessage;
    private bool _isBusy;
    private DateTimeOffset? _lastUpdated;
    private bool _hasLoadedOnce;
    private HealthCategory _selectedCategory = HealthCategory.Dbm;
    private readonly AutoRefreshTimer _autoRefresh;

    public HealthViewModel(
        SensorMonitor sensorMonitor,
        ISessionService session,
        ISettingsStore settings,
        IDeviceCache devices,
        IWindowService windows)
    {
        _sensorMonitor = sensorMonitor;
        _session = session;
        _settings = settings;
        _devices = devices;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Dbm = new SensorCategoryViewModel(windows, s => s.DbmThresholds, " dBm", "No dBm sensors found.");
        Signal = new SensorCategoryViewModel(windows, s => s.SignalThresholds, string.Empty, "No signal sensors found.");
        Temperature = new SensorCategoryViewModel(windows, s => s.TemperatureThresholds, " °C", "No temperature sensors found.");
        FanSpeed = new SensorCategoryViewModel(windows, s => s.FanSpeedThresholds, " RPM", "No fan speed sensors found.");

        RefreshCommand = new AsyncRelayCommand(() =>
        {
            _sensorMonitor.RequestRefresh();
            return Task.CompletedTask;
        }, () => _session.IsConnected && !IsBusy);

        ClearFiltersCommand = new RelayCommand(() => CurrentCategory.ClearFiltersCommand.Execute(null));

        SelectDbmCategoryCommand = new RelayCommand(() => SelectedCategory = HealthCategory.Dbm);
        SelectSignalCategoryCommand = new RelayCommand(() => SelectedCategory = HealthCategory.Signal);
        SelectTemperatureCategoryCommand = new RelayCommand(() => SelectedCategory = HealthCategory.Temperature);
        SelectFanSpeedCategoryCommand = new RelayCommand(() => SelectedCategory = HealthCategory.FanSpeed);

        _autoRefresh = new AutoRefreshTimer(() => OnPropertyChanged(nameof(NextRefreshText)));

        Dbm.PropertyChanged += OnCategoryPropertyChanged;
        Signal.PropertyChanged += OnCategoryPropertyChanged;
        Temperature.PropertyChanged += OnCategoryPropertyChanged;
        FanSpeed.PropertyChanged += OnCategoryPropertyChanged;

        _settings.Changed += OnSettingsChanged;
        _sensorMonitor.PollStarted += OnPollStarted;
        _sensorMonitor.Polled += OnPolled;
    }

    public SensorCategoryViewModel Dbm { get; }

    public SensorCategoryViewModel Signal { get; }

    public SensorCategoryViewModel Temperature { get; }

    public SensorCategoryViewModel FanSpeed { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>A short "45s" / "2:05" countdown to the next automatic refresh.</summary>
    public string NextRefreshText => PollAlignment.FormatRemaining(_sensorMonitor.SecondsUntilNextPoll());

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
    /// Called each time the tab is shown; starts the shared sensor monitor if
    /// nothing else has already (e.g. the Dashboard), and asks it to poll
    /// right away so this tab is not left empty until the next scheduled tick.
    /// </summary>
    public void OnShown()
    {
        _sensorMonitor.Start();

        // Always started, never gated on _hasLoadedOnce: this view model
        // subscribes to the shared monitor from its constructor, so a poll
        // triggered by another tab can already have marked it loaded before
        // this tab is ever shown - which previously skipped starting the
        // countdown entirely, leaving it frozen between polls.
        _autoRefresh.Start();

        if (_hasLoadedOnce)
        {
            return;
        }

        _sensorMonitor.RequestRefresh();
    }

    private void OnPollStarted(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() => IsBusy = true);

    private void OnPolled(object? sender, SensorPollResult result) => _dispatcher.InvokeAsync(() => ApplyPollResult(result));

    private void ApplyPollResult(SensorPollResult result)
    {
        IsBusy = false;
        OnPropertyChanged(nameof(NextRefreshText));

        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            StatusMessage = "Last refresh failed.";
            return;
        }

        ErrorMessage = null;

        var dbmSensors = result.Sensors.Where(s => s.HasClass("dbm")).ToList();
        var signalSensors = result.Sensors.Where(s => s.HasClass("signal")).ToList();
        var temperatureSensors = result.Sensors.Where(s => s.HasClass("temperature")).ToList();
        var fanSpeedSensors = result.Sensors.Where(s => s.HasClass("fanspeed")).ToList();

        var connection = _session.Connection;
        var settings = _settings.Current;
        string DeviceNameFor(int deviceId) => _devices.Get(deviceId)?.BestName ?? $"device {deviceId}";

        Dbm.Apply(dbmSensors, DeviceNameFor, connection, settings);
        Signal.Apply(signalSensors, DeviceNameFor, connection, settings);
        Temperature.Apply(temperatureSensors, DeviceNameFor, connection, settings);
        FanSpeed.Apply(fanSpeedSensors, DeviceNameFor, connection, settings);

        _hasLoadedOnce = true;
        _lastUpdated = result.CompletedAt;

        StatusMessage = $"{Dbm.TotalCount} dBm, {Signal.TotalCount} signal, {Temperature.TotalCount} temperature, {FanSpeed.TotalCount} fan speed.";
        OnPropertyChanged(nameof(LastUpdatedText));
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
        _sensorMonitor.PollStarted -= OnPollStarted;
        _sensorMonitor.Polled -= OnPolled;
    }
}

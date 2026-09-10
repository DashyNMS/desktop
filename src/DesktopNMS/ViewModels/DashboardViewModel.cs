using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// View model behind the Dashboard tab: a free-form canvas of widgets the user
/// can add, drag, resize, rename and remove (see <see cref="Widgets"/> and
/// <see cref="IsEditMode"/>). Widget types: "Sensors" (each showing whichever
/// sensors were added to it specifically, fed by the shared
/// <see cref="SensorMonitor"/> also used by the Health tab), "Alerts" and
/// "AlertsGauge" (fed by the app-wide <see cref="AlertMonitor"/>), and
/// "DeviceStatus" (fed by the shared <see cref="DeviceMonitor"/> also used by
/// the Devices tab). None of them trigger a fetch of their own - having any
/// combination open never costs more than one poll of each kind of data.
/// </summary>
public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly SensorMonitor _sensorMonitor;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly IWindowService _windows;
    private readonly IDashboardLayoutService _layout;
    private readonly AlertMonitor _alertMonitor;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DashboardWidgetViewModel> _widgetIndex = new();
    private readonly AutoRefreshTimer _autoRefresh;

    private string _statusMessage = "Not loaded yet.";
    private string? _errorMessage;
    private bool _isBusy;
    private bool _isEditMode;
    private DateTimeOffset? _lastUpdated;
    private bool _hasLoadedOnce;

    /// <summary>The last successful fetch, kept so a brand-new widget can be
    /// seeded instantly (see <see cref="OnLayoutChanged"/>) instead of waiting
    /// for the next poll.</summary>
    private IReadOnlyList<Sensor> _lastFleet = Array.Empty<Sensor>();
    private Func<int, string> _lastDeviceNameFor = id => $"device {id}";

    public DashboardViewModel(
        SensorMonitor sensorMonitor,
        ISessionService session,
        ISettingsStore settings,
        IDeviceCache devices,
        IWindowService windows,
        IDashboardLayoutService layout,
        AlertMonitor alertMonitor,
        DeviceMonitor deviceMonitor,
        ILogger<DashboardViewModel> logger)
    {
        _sensorMonitor = sensorMonitor;
        _session = session;
        _settings = settings;
        _devices = devices;
        _windows = windows;
        _layout = layout;
        _alertMonitor = alertMonitor;
        _deviceMonitor = deviceMonitor;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Widgets = new ObservableCollection<DashboardWidgetViewModel>();

        RefreshCommand = new AsyncRelayCommand(() =>
        {
            _sensorMonitor.RequestRefresh();
            return Task.CompletedTask;
        }, () => _session.IsConnected && !IsBusy);

        OpenDeviceCommand = new RelayCommand(parameter =>
        {
            if (parameter is SensorItemViewModel { DeviceUrl: { } url })
            {
                _windows.OpenUrl(url);
            }
        });

        AddSensorWidgetCommand = new RelayCommand(() => _layout.AddWidget("Sensors", "Sensors"));
        AddAlertsWidgetCommand = new RelayCommand(() => _layout.AddWidget("Alerts", "Alerts"));
        AddAlertsGaugeWidgetCommand = new RelayCommand(() => _layout.AddWidget("AlertsGauge", "Alerts gauge"));
        AddDeviceStatusWidgetCommand = new RelayCommand(() => _layout.AddWidget("DeviceStatus", "Device status"));

        _autoRefresh = new AutoRefreshTimer(() => OnPropertyChanged(nameof(NextRefreshText)));

        SyncWidgets();

        _settings.Changed += OnSettingsChanged;
        _layout.Changed += OnLayoutChanged;
        _sensorMonitor.PollStarted += OnPollStarted;
        _sensorMonitor.Polled += OnPolled;
    }

    /// <summary>The widgets on the canvas, in the order they were created.</summary>
    public ObservableCollection<DashboardWidgetViewModel> Widgets { get; }

    public RelayCommand AddSensorWidgetCommand { get; }

    public RelayCommand AddAlertsWidgetCommand { get; }

    public RelayCommand AddAlertsGaugeWidgetCommand { get; }

    public RelayCommand AddDeviceStatusWidgetCommand { get; }

    /// <summary>True while the user is arranging the dashboard: widgets show drag/resize/remove handles.</summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        set => SetProperty(ref _isEditMode, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Opens a sensor's device in LibreNMS. Shared by every Sensors widget, since it needs no per-widget state.</summary>
    public RelayCommand OpenDeviceCommand { get; }

    /// <summary>A short "45s" / "2:05" countdown to the next automatic refresh.</summary>
    public string NextRefreshText => PollAlignment.FormatRemaining(_sensorMonitor.SecondsUntilNextPoll());

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

    /// <summary>
    /// Called each time the tab is shown; starts the shared sensor monitor if
    /// nothing else has already (e.g. the Health tab), and asks it to poll
    /// right away so this tab is not left empty until the next scheduled tick.
    /// </summary>
    public void OnShown()
    {
        _sensorMonitor.Start();

        // Always started, never gated on _hasLoadedOnce - see the matching
        // comment in HealthViewModel: a poll triggered by another tab can mark
        // this one loaded before it is ever shown, which would otherwise skip
        // starting the countdown and leave it frozen between polls.
        _autoRefresh.Start();

        if (_hasLoadedOnce)
        {
            return;
        }

        _sensorMonitor.RequestRefresh();
    }

    /// <summary>
    /// Called by the view whenever the canvas's actual rendered size is known
    /// or changes (initial load, window resize, or the app moving to a
    /// different-sized display) so the layout can be rescaled to fit.
    /// </summary>
    public void NotifyViewportSize(double width, double height) => _layout.EnsureFitsViewport(width, height);

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

        if (Widgets.Count == 0)
        {
            _hasLoadedOnce = true;
            StatusMessage = "No widgets yet.";
            return;
        }

        // Alerts/AlertsGauge widgets need no data from here - they listen to
        // the app-wide AlertMonitor directly.
        var sensorWidgets = Widgets.OfType<SensorWidgetViewModel>().ToList();
        if (sensorWidgets.Count == 0)
        {
            _hasLoadedOnce = true;
            StatusMessage = "Up to date.";
            return;
        }

        // Only classes the app understands thresholds for - see SensorCategoryRegistry.
        var supported = result.Sensors.Where(s => SensorCategoryRegistry.Resolve(s.SensorClass) is not null).ToList();

        var connection = _session.Connection;
        var settings = _settings.Current;
        string DeviceNameFor(int deviceId) => _devices.Get(deviceId)?.BestName ?? $"device {deviceId}";

        foreach (var widget in sensorWidgets)
        {
            widget.ApplyFleet(supported, DeviceNameFor, connection, settings);
        }

        _lastFleet = supported;
        _lastDeviceNameFor = DeviceNameFor;
        _hasLoadedOnce = true;
        _lastUpdated = result.CompletedAt;

        var shown = sensorWidgets.Sum(w => w.Sensors.Count);
        StatusMessage = $"{shown} sensor(s) across {sensorWidgets.Count} widget(s).";
        OnPropertyChanged(nameof(LastUpdatedText));
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        foreach (var widget in Widgets.OfType<SensorWidgetViewModel>())
        {
            widget.ApplyThresholds(settings);
        }
    }

    // ---------------------------------------------------------------- widgets

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        var previousIds = Widgets.Select(w => w.Id).ToHashSet();
        SyncWidgets();

        // A brand new widget has nothing to show yet (SyncFrom only re-applies
        // a widget's own last-seen fleet). Seed it from the last real fetch
        // immediately - e.g. adding a widget or a sensor should feel instant,
        // not wait for the next poll - rather than hitting the API again for
        // what is purely a local layout/membership change.
        foreach (var widget in Widgets.OfType<SensorWidgetViewModel>())
        {
            if (!previousIds.Contains(widget.Id))
            {
                widget.ApplyFleet(_lastFleet, _lastDeviceNameFor, _session.Connection, _settings.Current);
            }
        }
    }

    /// <summary>Adds/removes/updates <see cref="Widgets"/> to match the persisted layout.</summary>
    private void SyncWidgets()
    {
        var models = _layout.Widgets;
        var incomingIds = models.Select(w => w.Id).ToHashSet();

        for (var i = Widgets.Count - 1; i >= 0; i--)
        {
            var widget = Widgets[i];
            if (!incomingIds.Contains(widget.Id))
            {
                _widgetIndex.Remove(widget.Id);
                Widgets.RemoveAt(i);
            }
        }

        foreach (var model in models)
        {
            if (_widgetIndex.TryGetValue(model.Id, out var existing))
            {
                existing.SyncFrom(model);
            }
            else
            {
                var created = CreateWidgetViewModel(model);
                _widgetIndex[model.Id] = created;
                Widgets.Add(created);
            }
        }
    }

    private DashboardWidgetViewModel CreateWidgetViewModel(DashboardWidget model) => model.WidgetType switch
    {
        "Alerts" => new AlertsWidgetViewModel(_layout, model, _alertMonitor, _session, _settings, _devices, _windows),
        "AlertsGauge" => new AlertsGaugeWidgetViewModel(_layout, model, _alertMonitor),
        "DeviceStatus" => new DeviceStatusWidgetViewModel(_layout, model, _deviceMonitor),
        // "Sensors" (and any future/unknown type, so a layout from a newer
        // version does not blow up) fall back to the Sensors widget.
        _ => new SensorWidgetViewModel(_layout, model, OpenDeviceCommand),
    };

    public void Dispose()
    {
        _autoRefresh.Dispose();
        _settings.Changed -= OnSettingsChanged;
        _layout.Changed -= OnLayoutChanged;
        _sensorMonitor.PollStarted -= OnPollStarted;
        _sensorMonitor.Polled -= OnPolled;

        foreach (var widget in Widgets)
        {
            (widget as IDisposable)?.Dispose();
        }
    }
}

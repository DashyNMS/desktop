using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The empty Dashboard's welcome card (#233): what you're connected to, a
/// starter dashboard or the widget picker, shortcuts into the app, and a
/// short setup checklist that ticks itself off. It goes once the first
/// widget is added, or for good with "Don't show again".
/// </summary>
public sealed class WelcomeViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IDashboardLayoutService _layout;
    private readonly IWindowService _windows;
    private readonly IDeviceCache _devices;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly AlertMonitor _alertMonitor;
    private readonly IGraylogApi _graylog;
    private readonly IUnimusApi _unimus;
    private readonly Dispatcher _dispatcher;

    private int? _activeAlerts;
    private IReadOnlyList<WelcomeStep> _steps = Array.Empty<WelcomeStep>();

    public WelcomeViewModel(
        ISettingsStore settings,
        IDashboardLayoutService layout,
        IWindowService windows,
        IDeviceCache devices,
        DeviceMonitor deviceMonitor,
        AlertMonitor alertMonitor,
        IGraylogApi graylog,
        IUnimusApi unimus,
        ICommand chooseWidgets)
    {
        _settings = settings;
        _layout = layout;
        _windows = windows;
        _devices = devices;
        _deviceMonitor = deviceMonitor;
        _alertMonitor = alertMonitor;
        _graylog = graylog;
        _unimus = unimus;
        _dispatcher = Dispatcher.CurrentDispatcher;

        ChooseWidgetsCommand = chooseWidgets;
        UseStarterCommand = new RelayCommand(() => _layout.AddWidgets(StarterDashboard.Create()));
        OpenTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is MainTab tab)
            {
                _windows.ShowMainTab(tab);
            }
        });
        DismissCommand = new RelayCommand(() =>
        {
            _settings.Current.WelcomeDismissed = true;
            _settings.Save();
        });

        _deviceMonitor.Polled += OnDevicesPolled;
        _alertMonitor.Polled += OnAlertsPolled;
        _settings.Changed += OnSettingsChanged;
        _graylog.ConfigurationChanged += OnIntegrationChanged;

        RebuildSteps();
    }

    /// <summary>"412 devices · 9 active alerts", filling in as each list arrives.</summary>
    public string ServerSummary
    {
        get
        {
            var parts = new List<string>();
            if (_devices.IsLoaded)
            {
                parts.Add(Count(_devices.All.Count, "device", "devices"));
            }

            if (_activeAlerts is { } alerts)
            {
                parts.Add(Count(alerts, "active alert", "active alerts"));
            }

            return parts.Count == 0
                ? "Connected to your LibreNMS server"
                : "Connected to your LibreNMS server · " + string.Join(" · ", parts);
        }
    }

    public string StarterContents => "Alerts gauge, Device status, Alerts, Top interfaces and Recently viewed";

    public RelayCommand UseStarterCommand { get; }

    public ICommand ChooseWidgetsCommand { get; }

    /// <summary>Jump-in shortcuts: the parameter is the <see cref="MainTab"/>.</summary>
    public RelayCommand OpenTabCommand { get; }

    public RelayCommand DismissCommand { get; }

    public IReadOnlyList<WelcomeStep> Steps
    {
        get => _steps;
        private set
        {
            _steps = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StepsText));
        }
    }

    /// <summary>"1 of 4".</summary>
    public string StepsText => $"{_steps.Count(s => s.IsDone)} of {_steps.Count}";

    private static string Count(int count, string one, string many)
        => count.ToString("N0", CultureInfo.CurrentCulture) + " " + (count == 1 ? one : many);

    private void RebuildSteps()
    {
        Steps = new[]
        {
            new WelcomeStep("Sign in to LibreNMS", isDone: true, actionText: null, action: null),
            new WelcomeStep("Connect Graylog for device logs", _graylog.IsConfigured, "Set up",
                new RelayCommand(() => _windows.ShowSettingsSection(SettingsSection.Integrations))),
            new WelcomeStep("Connect Unimus for config backups", _unimus.IsConfigured, "Set up",
                new RelayCommand(() => _windows.ShowSettingsSection(SettingsSection.Integrations))),
            new WelcomeStep("Pin the devices you watch most", _settings.Current.PinnedDevices.Count > 0, "Devices",
                new RelayCommand(() => _windows.ShowMainTab(MainTab.Devices))),
        };
    }

    private void OnDevicesPolled(object? sender, DevicePollResult result)
        => _dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(ServerSummary)));

    private void OnAlertsPolled(object? sender, AlertPollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        var active = result.Alerts.Count(a => a.State == AlertState.Active);
        _dispatcher.InvokeAsync(() =>
        {
            _activeAlerts = active;
            OnPropertyChanged(nameof(ServerSummary));
        });
    }

    // Unimus is set up from the settings once they're saved, so look again
    // after that has had a chance to run.
    private void OnSettingsChanged(object? sender, AppSettings settings)
        => _dispatcher.InvokeAsync(RebuildSteps, DispatcherPriority.Background);

    private void OnIntegrationChanged(object? sender, EventArgs e)
        => _dispatcher.InvokeAsync(RebuildSteps, DispatcherPriority.Background);

    public void Dispose()
    {
        _deviceMonitor.Polled -= OnDevicesPolled;
        _alertMonitor.Polled -= OnAlertsPolled;
        _settings.Changed -= OnSettingsChanged;
        _graylog.ConfigurationChanged -= OnIntegrationChanged;
    }
}

/// <summary>One line of the welcome card's setup checklist.</summary>
public sealed class WelcomeStep
{
    public WelcomeStep(string text, bool isDone, string? actionText, ICommand? action)
    {
        Text = text;
        IsDone = isDone;
        ActionText = actionText;
        Action = action;
    }

    public string Text { get; }

    public bool IsDone { get; }

    /// <summary>"Set up", "Devices" - hidden once done.</summary>
    public string? ActionText { get; }

    public ICommand? Action { get; }

    public bool ShowAction => !IsDone && Action is not null;
}

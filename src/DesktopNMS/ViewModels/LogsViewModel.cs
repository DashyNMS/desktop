using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The main window's Logs tab (issue #114). Graylog is its only view for
/// now - LibreNMS's Overview, Graylog page: every device's messages, with
/// device, stream, level, range and text filters and auto-update - but the
/// tab is Logs rather than Graylog so other log sources can sit alongside it
/// later (with a hover menu, like Maps, once there's more than one). The tab
/// only shows while Graylog is set up - see MainViewModel.ShowLogsTab.
/// </summary>
public sealed class LogsViewModel : ObservableObject
{
    private readonly DeviceMonitor _deviceMonitor;
    private readonly Dispatcher _dispatcher;

    public LogsViewModel(
        IGraylogApi graylog,
        ILibreNmsClient client,
        IDeviceCache deviceCache,
        ISettingsStore settings,
        IWindowService windows,
        DeviceMonitor deviceMonitor,
        ILogger<LogsViewModel> logger)
    {
        _deviceMonitor = deviceMonitor;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Graylog = GraylogMessagesViewModel.ForFleet(graylog, client, deviceCache, settings, windows, logger);
        Fleet = new FleetLogsViewModel(client, deviceCache, settings, windows, logger);
        ShowEventLogCommand = new RelayCommand(() => SelectedView = LogsSection.EventLog);
        ShowAlertLogCommand = new RelayCommand(() => SelectedView = LogsSection.AlertLog);
        ShowGraylogCommand = new RelayCommand(() => SelectedView = LogsSection.Graylog);

        // The device filter follows the shared device poll rather than
        // fetching its own list.
        _deviceMonitor.Polled += OnDevicesPolled;
    }

    public GraylogMessagesViewModel Graylog { get; }

    /// <summary>LibreNMS's event log and alert log for every device (#287).</summary>
    public FleetLogsViewModel Fleet { get; }

    private LogsSection _selectedView = LogsSection.EventLog;

    /// <summary>Event log, Alert log or Graylog - the switch across the top.</summary>
    public LogsSection SelectedView
    {
        get => _selectedView;
        set
        {
            if (!SetProperty(ref _selectedView, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsEventLogSelected));
            OnPropertyChanged(nameof(IsAlertLogSelected));
            OnPropertyChanged(nameof(IsGraylogSelected));
            OnPropertyChanged(nameof(IsFleetLogSelected));

            if (value == LogsSection.Graylog)
            {
                Graylog.Activate();
            }
            else
            {
                Graylog.Deactivate();
                Fleet.ShowingEventLog = value == LogsSection.EventLog;
                Fleet.Activate();
            }
        }
    }

    public bool IsEventLogSelected
    {
        get => _selectedView == LogsSection.EventLog;
        set { if (value) SelectedView = LogsSection.EventLog; }
    }

    public bool IsAlertLogSelected
    {
        get => _selectedView == LogsSection.AlertLog;
        set { if (value) SelectedView = LogsSection.AlertLog; }
    }

    public bool IsGraylogSelected
    {
        get => _selectedView == LogsSection.Graylog;
        set { if (value) SelectedView = LogsSection.Graylog; }
    }

    public bool IsFleetLogSelected => _selectedView != LogsSection.Graylog;

    public RelayCommand ShowEventLogCommand { get; }

    public RelayCommand ShowAlertLogCommand { get; }

    public RelayCommand ShowGraylogCommand { get; }

    public RelayCommand ClearFiltersCommand => IsGraylogSelected ? Graylog.ClearFiltersCommand : Fleet.ClearFiltersCommand;

    public AsyncRelayCommand RefreshCommand => IsGraylogSelected ? Graylog.RefreshCommand : Fleet.RefreshCommand;

    /// <summary>The tab has been shown (or the window brought back while it's showing).</summary>
    public void OnShown()
    {
        _deviceMonitor.Start();

        // Only "All devices" so far - ask for the list rather than waiting
        // for the next scheduled poll.
        if (Graylog.DeviceOptions.Count <= 1)
        {
            _deviceMonitor.RequestRefresh();
        }

        if (IsGraylogSelected)
        {
            Graylog.Activate();
        }
        else
        {
            Fleet.Activate();
        }
    }

    /// <summary>The tab has been left, or the window hidden - auto-update stops until it's shown again.</summary>
    public void OnHidden() => Graylog.Deactivate();

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            Graylog.UpdateDevices(result.Devices);
            Fleet.SetDevices(result.Devices);
        });
    }
}

/// <summary>The Logs tab's views (#287).</summary>
public enum LogsSection
{
    EventLog,
    AlertLog,
    Graylog,
}

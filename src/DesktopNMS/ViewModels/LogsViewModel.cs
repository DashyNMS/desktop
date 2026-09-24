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

        // The device filter follows the shared device poll rather than
        // fetching its own list.
        _deviceMonitor.Polled += OnDevicesPolled;
    }

    public GraylogMessagesViewModel Graylog { get; }

    public RelayCommand ClearFiltersCommand => Graylog.ClearFiltersCommand;

    public AsyncRelayCommand RefreshCommand => Graylog.RefreshCommand;

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

        Graylog.Activate();
    }

    /// <summary>The tab has been left, or the window hidden - auto-update stops until it's shown again.</summary>
    public void OnHidden() => Graylog.Deactivate();

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() => Graylog.UpdateDevices(result.Devices));
    }
}

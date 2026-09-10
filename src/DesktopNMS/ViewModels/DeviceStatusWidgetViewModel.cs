using System;
using System.Windows.Threading;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The "Device status" dashboard widget: ring gauges for up, down,
/// maintenance and disabled/ignored device counts, mirroring the Alerts
/// gauge widget. Unlike the Alerts widgets, <see cref="DeviceMonitor"/> is
/// lazily started (only the Devices tab used to start it), so this widget
/// starts it itself - it may be the only thing on the Dashboard asking for
/// device data at all.
/// </summary>
public sealed class DeviceStatusWidgetViewModel : DashboardWidgetViewModel, IDisposable
{
    private readonly DeviceMonitor _monitor;
    private readonly Dispatcher _dispatcher;

    private int _upCount;
    private int _downCount;
    private int _maintenanceCount;
    private int _disabledCount;
    private int _totalCount;

    public DeviceStatusWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, DeviceMonitor monitor)
        : base(layout, model)
    {
        _monitor = monitor;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _monitor.Polled += OnPolled;

        // Lazily started, unlike AlertMonitor - a Dashboard-only user who
        // never opens the Devices tab still needs this running.
        _monitor.Start();
        _monitor.RequestRefresh();
    }

    public int UpCount => _upCount;

    public int DownCount => _downCount;

    public int MaintenanceCount => _maintenanceCount;

    /// <summary>Covers both Disabled and Ignored, same as the Devices tab's own grouping.</summary>
    public int DisabledCount => _disabledCount;

    public int TotalCount => _totalCount;

    public double UpFraction => _totalCount > 0 ? (double)_upCount / _totalCount : 0;

    public double DownFraction => _totalCount > 0 ? (double)_downCount / _totalCount : 0;

    public double MaintenanceFraction => _totalCount > 0 ? (double)_maintenanceCount / _totalCount : 0;

    public double DisabledFraction => _totalCount > 0 ? (double)_disabledCount / _totalCount : 0;

    public bool HasNoDevices => _totalCount == 0;

    private void OnPolled(object? sender, DevicePollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() => Apply(result));
    }

    private void Apply(DevicePollResult result)
    {
        var maintenanceIds = result.DeviceIdsUnderMaintenance;

        var up = 0;
        var down = 0;
        var maintenance = 0;
        var disabled = 0;

        foreach (var device in result.Devices)
        {
            if (maintenanceIds.Contains(device.DeviceId))
            {
                maintenance++;
                continue;
            }

            switch (device.State)
            {
                case DeviceState.Up:
                    up++;
                    break;
                case DeviceState.Down:
                    down++;
                    break;
                default:
                    disabled++;
                    break;
            }
        }

        _upCount = up;
        _downCount = down;
        _maintenanceCount = maintenance;
        _disabledCount = disabled;
        _totalCount = result.Devices.Count;

        OnPropertyChanged(nameof(UpCount));
        OnPropertyChanged(nameof(DownCount));
        OnPropertyChanged(nameof(MaintenanceCount));
        OnPropertyChanged(nameof(DisabledCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(UpFraction));
        OnPropertyChanged(nameof(DownFraction));
        OnPropertyChanged(nameof(MaintenanceFraction));
        OnPropertyChanged(nameof(DisabledFraction));
        OnPropertyChanged(nameof(HasNoDevices));
    }

    public void Dispose() => _monitor.Polled -= OnPolled;
}

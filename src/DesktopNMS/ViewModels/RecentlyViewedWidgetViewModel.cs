using System;
using System.Collections.ObjectModel;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The "Recently viewed" dashboard widget: the same devices as the Devices
/// tab's own recently-viewed strip (see
/// <see cref="AppSettings.RecentlyViewedDevices"/>), independent of that
/// strip's own show/hide setting - a widget is already opt-in by the user
/// adding it, so <see cref="AppSettings.ShowRecentlyViewedDevices"/> (which
/// only governs the Devices tab) does not also hide this.
/// </summary>
public sealed class RecentlyViewedWidgetViewModel : DashboardWidgetViewModel, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly Action<int> _openDevice;

    public RecentlyViewedWidgetViewModel(
        IDashboardLayoutService layout,
        DashboardWidget model,
        ISettingsStore settings,
        IDeviceCache devices,
        DeviceMonitor deviceMonitor,
        Action<int> openDevice)
        : base(layout, model)
    {
        _settings = settings;
        _devices = devices;
        _deviceMonitor = deviceMonitor;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _openDevice = openDevice;

        Devices = new ObservableCollection<RecentlyViewedDeviceItemViewModel>();
        Rebuild();

        _settings.Changed += OnSettingsChanged;

        // Each row's status dot and "hardware 00B7 location" come from the
        // device list, so refresh with it (#212).
        _deviceMonitor.Polled += OnDevicesPolled;
        _deviceMonitor.Start();
    }

    public ObservableCollection<RecentlyViewedDeviceItemViewModel> Devices { get; }

    public bool HasDevices => Devices.Count > 0;

    private void OnSettingsChanged(object? sender, AppSettings settings) => Rebuild();

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (result.Succeeded)
        {
            _dispatcher.InvokeAsync(Rebuild);
        }
    }

    private void Rebuild()
    {
        Devices.Clear();

        foreach (var entry in _settings.Current.RecentlyViewedDevices)
        {
            Devices.Add(new RecentlyViewedDeviceItemViewModel(entry, _openDevice, _devices.Get(entry.DeviceId)));
        }

        OnPropertyChanged(nameof(HasDevices));
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _deviceMonitor.Polled -= OnDevicesPolled;
    }
}

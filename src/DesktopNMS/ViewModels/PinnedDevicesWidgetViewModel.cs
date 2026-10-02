using System;
using System.Collections.ObjectModel;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The "Pinned devices" dashboard widget: the same devices pinned to the top
/// of the Devices tab's grid (see <see cref="AppSettings.PinnedDevices"/>),
/// with its own Unpin action so a device can be un-favourited from here
/// without needing the Devices tab open.
/// </summary>
public sealed class PinnedDevicesWidgetViewModel : DashboardWidgetViewModel, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly Action<int> _openDevice;

    public PinnedDevicesWidgetViewModel(
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

        Devices = new ObservableCollection<PinnedDeviceItemViewModel>();
        Rebuild();

        _settings.Changed += OnSettingsChanged;

        // Each row's status dot and "hardware 00B7 location" come from the
        // device list, so refresh with it (#212).
        _deviceMonitor.Polled += OnDevicesPolled;
        _deviceMonitor.Start();
    }

    public ObservableCollection<PinnedDeviceItemViewModel> Devices { get; }

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

        foreach (var entry in _settings.Current.PinnedDevices)
        {
            Devices.Add(new PinnedDeviceItemViewModel(entry, _openDevice, Unpin, _devices.Get(entry.DeviceId)));
        }

        OnPropertyChanged(nameof(HasDevices));
    }

    private void Unpin(int deviceId)
    {
        var pinned = _settings.Current.PinnedDevices;

        if (pinned.RemoveAll(p => p.DeviceId == deviceId) > 0)
        {
            _settings.Save();
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _deviceMonitor.Polled -= OnDevicesPolled;
    }
}

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
    private readonly Action<int> _openDevice;

    public PinnedDevicesWidgetViewModel(
        IDashboardLayoutService layout,
        DashboardWidget model,
        ISettingsStore settings,
        Action<int> openDevice)
        : base(layout, model)
    {
        _settings = settings;
        _openDevice = openDevice;

        Devices = new ObservableCollection<PinnedDeviceItemViewModel>();
        Rebuild();

        _settings.Changed += OnSettingsChanged;
    }

    public ObservableCollection<PinnedDeviceItemViewModel> Devices { get; }

    public bool HasDevices => Devices.Count > 0;

    private void OnSettingsChanged(object? sender, AppSettings settings) => Rebuild();

    private void Rebuild()
    {
        Devices.Clear();

        foreach (var entry in _settings.Current.PinnedDevices)
        {
            Devices.Add(new PinnedDeviceItemViewModel(entry, _openDevice, Unpin));
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

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}

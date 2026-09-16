using System;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One entry in the Dashboard's Pinned devices widget - see <see cref="PinnedDevicesWidgetViewModel"/>.</summary>
public sealed class PinnedDeviceItemViewModel
{
    public PinnedDeviceItemViewModel(PinnedDevice entry, Action<int> onOpen, Action<int> onUnpin)
    {
        DeviceId = entry.DeviceId;
        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? $"Device {entry.DeviceId}" : entry.DisplayName!;
        OpenCommand = new RelayCommand(() => onOpen(DeviceId));
        UnpinCommand = new RelayCommand(() => onUnpin(DeviceId));
    }

    public int DeviceId { get; }

    public string DisplayName { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand UnpinCommand { get; }
}

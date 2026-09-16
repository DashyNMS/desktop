using System;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One chip in the Devices tab's "recently viewed" strip. Owns its own
/// command (rather than binding back to a parent-view-model command via
/// RelativeSource, unused anywhere in this codebase) so the ItemsControl
/// template in DevicesView.xaml stays a simple data binding.
/// </summary>
public sealed class RecentlyViewedDeviceItemViewModel
{
    public RecentlyViewedDeviceItemViewModel(RecentlyViewedDevice entry, Action<int> onOpen)
    {
        DeviceId = entry.DeviceId;
        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? $"Device {entry.DeviceId}" : entry.DisplayName!;
        OpenCommand = new RelayCommand(() => onOpen(DeviceId));
    }

    public int DeviceId { get; }

    public string DisplayName { get; }

    public RelayCommand OpenCommand { get; }
}

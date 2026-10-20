using System;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One entry in the Dashboard's Pinned devices widget - see <see cref="PinnedDevicesWidgetViewModel"/>.</summary>
public sealed class PinnedDeviceItemViewModel
{
    /// <param name="device">The device as LibreNMS last reported it, for the status dot and "hardware · location" (#212); null until the device list has loaded.</param>
    public PinnedDeviceItemViewModel(PinnedDevice entry, Action<int> onOpen, Action<int> onUnpin, Device? device = null)
    {
        DeviceId = entry.DeviceId;
        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? $"Device {entry.DeviceId}" : entry.DisplayName!;
        State = device?.State;
        Subtitle = DeviceRowDetails.Subtitle(device);
        OpenCommand = new RelayCommand(() => onOpen(DeviceId));
        UnpinCommand = new RelayCommand(() => onUnpin(DeviceId));
    }

    public int DeviceId { get; }

    public string DisplayName { get; }

    public DeviceState? State { get; }

    public bool HasState => State is not null;

    public string? Subtitle { get; }

    public bool HasSubtitle => Subtitle is not null;

    public RelayCommand OpenCommand { get; }

    public RelayCommand UnpinCommand { get; }
}

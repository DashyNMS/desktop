using System;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One recently viewed device: a chip in the Devices tab's strip, or a row in
/// the Dashboard's Recently viewed widget. Owns its own command (rather than
/// binding back to a parent-view-model command via RelativeSource, unused
/// anywhere in this codebase) so the templates stay simple data bindings.
/// </summary>
public sealed class RecentlyViewedDeviceItemViewModel
{
    /// <param name="device">The device as LibreNMS last reported it, for the widget's status dot and "hardware · location" (#212); null until the device list has loaded.</param>
    public RecentlyViewedDeviceItemViewModel(RecentlyViewedDevice entry, Action<int> onOpen, Device? device = null)
    {
        DeviceId = entry.DeviceId;
        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? $"Device {entry.DeviceId}" : entry.DisplayName!;
        ViewedText = DeviceRowDetails.Ago(entry.ViewedAt);
        State = device?.State;
        Subtitle = DeviceRowDetails.Subtitle(device);
        OpenCommand = new RelayCommand(() => onOpen(DeviceId));
    }

    public int DeviceId { get; }

    public string DisplayName { get; }

    /// <summary>"5m ago" - when it was last opened.</summary>
    public string ViewedText { get; }

    public DeviceState? State { get; }

    public bool HasState => State is not null;

    public string? Subtitle { get; }

    public bool HasSubtitle => Subtitle is not null;

    public RelayCommand OpenCommand { get; }
}

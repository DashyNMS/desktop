using System;
using System.Globalization;
using DesktopNMS.Core;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in the device list.</summary>
public sealed class DeviceItemViewModel : ObservableObject
{
    private Device _device;
    private DeviceNameStyle _nameStyle;
    private bool _isUnderMaintenance;
    private bool _isPinned;
    private bool _serverTimestampsAreUtc;

    public DeviceItemViewModel(Device device, DeviceNameStyle nameStyle, Action<int> onTogglePin)
    {
        _device = device;
        _nameStyle = nameStyle;
        TogglePinCommand = new RelayCommand(() => onTogglePin(DeviceId));
    }

    public int DeviceId => _device.DeviceId;

    public Device Model => _device;

    /// <summary>
    /// True while this device is pinned to the top of the Devices tab (see
    /// DeviceListViewModel.TogglePin). Kept in step with AppSettings.PinnedDevices
    /// rather than owning the fact itself, the same relationship
    /// <see cref="IsUnderMaintenance"/> has with its own source of truth.
    /// </summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    public RelayCommand TogglePinCommand { get; }

    /// <summary>
    /// True when LibreNMS reports this device inside an active maintenance
    /// window. Set separately from <see cref="Update"/>: it comes from its own
    /// per-device API call, not the device list response, and is on its own
    /// refresh cycle (see <c>DeviceListViewModel</c>).
    /// </summary>
    public bool IsUnderMaintenance
    {
        get => _isUnderMaintenance;
        set
        {
            if (SetProperty(ref _isUnderMaintenance, value))
            {
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    /// <summary>
    /// The state to show. Maintenance overrides whatever LibreNMS's raw
    /// status/disabled/ignore flags say - a device that is deliberately
    /// offline for maintenance should read as "Maintenance", not "Down".
    /// </summary>
    public DeviceState State => _isUnderMaintenance ? DeviceState.Maintenance : _device.State;

    public string StateText => State.ToDisplayString();

    /// <summary>The device name, following the same hostname/sysName/display preference as the alert list.</summary>
    public string Name => _nameStyle.Resolve(_device, _device.Hostname);

    /// <summary>The other name, shown as a subtitle. Null when it would just repeat <see cref="Name"/>.</summary>
    public string? AlternateName => _nameStyle.ResolveSecondary(_device, _device.Hostname, Name);

    public bool HasAlternateName => AlternateName is not null;

    public string Ip => Blank(_device.Ip);

    public string Os => Blank(_device.Os);

    public string Hardware => Blank(_device.Hardware);

    public string Location => Blank(_device.Location);

    /// <summary>LibreNMS's own type values are lowercase ("network", "wireless", ...) - capitalised here, same as the Devices tab's type filter and Device Details' own Overview card.</summary>
    public string Type => TitleCase(_device.Type);

    public string Contact => Blank(_device.Contact);

    public string Serial => Blank(_device.Serial);

    /// <summary>Optional grid column (issue #40) - an absolute timestamp rather than a "X ago" style, since a sortable column reads better as a fixed value than one that keeps re-rendering as time passes.</summary>
    public string LastDiscoveredText => _device.LastDiscovered is { } t
        ? ServerTime.ToLocal(t, ServerTimestampsAreUtc).ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
        : "-";

    /// <summary>Settings' "server stores timestamps in UTC" - see <see cref="ServerTime"/> (#150).</summary>
    public bool ServerTimestampsAreUtc
    {
        get => _serverTimestampsAreUtc;
        set
        {
            if (SetProperty(ref _serverTimestampsAreUtc, value))
            {
                OnPropertyChanged(nameof(LastDiscoveredText));
            }
        }
    }

    public string UptimeText => _device.State == DeviceState.Up ? FormatUptime(_device.Uptime) : "-";

    /// <summary>Everything a search box should match against.</summary>
    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (AlternateName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || Ip.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Os.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Hardware.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Location.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Type.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Contact.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Serial.Contains(term, StringComparison.OrdinalIgnoreCase)
        || DeviceId.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal);

    /// <summary>Replaces the underlying device in place so the selection survives a refresh.</summary>
    public void Update(Device device, DeviceNameStyle nameStyle)
    {
        _device = device;
        _nameStyle = nameStyle;
        RaiseAllChanged();
    }

    /// <summary>Re-evaluates the name only, e.g. after the naming preference changed in Settings.</summary>
    public void ApplyNameStyle(DeviceNameStyle nameStyle)
    {
        _nameStyle = nameStyle;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AlternateName));
        OnPropertyChanged(nameof(HasAlternateName));
    }

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value!;

    private static string TitleCase(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : char.ToUpperInvariant(value[0]) + value[1..];

    private static string FormatUptime(long seconds)
    {
        if (seconds <= 0)
        {
            return "-";
        }

        var span = TimeSpan.FromSeconds(seconds);

        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    private void RaiseAllChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AlternateName));
        OnPropertyChanged(nameof(HasAlternateName));
        OnPropertyChanged(nameof(Ip));
        OnPropertyChanged(nameof(Os));
        OnPropertyChanged(nameof(Hardware));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(Contact));
        OnPropertyChanged(nameof(Serial));
        OnPropertyChanged(nameof(LastDiscoveredText));
        OnPropertyChanged(nameof(UptimeText));
    }
}

namespace DesktopNMS.Core.Models;

/// <summary>
/// The single state a device is in. <see cref="Up"/>, <see cref="Down"/>,
/// <see cref="Disabled"/> and <see cref="Ignored"/> come from the three flags
/// LibreNMS reports on every device (status, disabled, ignore) - see
/// <see cref="Device.State"/>. <see cref="Maintenance"/> does not: it comes
/// from a separate, per-device API call (there is no bulk endpoint for it),
/// so it is layered on top by the device list view model rather than being
/// part of <see cref="Device.State"/> itself.
/// </summary>
public enum DeviceState
{
    Up,
    Down,

    /// <summary>Polling turned off by an operator.</summary>
    Disabled,

    /// <summary>Polled, but excluded from alerting and graphs.</summary>
    Ignored,

    /// <summary>Inside an active LibreNMS maintenance window.</summary>
    Maintenance,
}

public static class DeviceStateExtensions
{
    public static string ToDisplayString(this DeviceState state) => state switch
    {
        DeviceState.Up => "Up",
        DeviceState.Down => "Down",
        DeviceState.Disabled => "Disabled",
        DeviceState.Ignored => "Ignored",
        DeviceState.Maintenance => "Maintenance",
        _ => "Unknown",
    };
}

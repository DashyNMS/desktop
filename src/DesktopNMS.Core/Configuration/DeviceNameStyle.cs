using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Configuration;

/// <summary>
/// Which device name to show in the alert list.
/// </summary>
/// <remarks>
/// The alerts endpoint only returns <c>hostname</c>, so anything else requires
/// the device list to have been loaded. Every option falls back to hostname
/// when its preferred field is empty or the device is not cached yet, so the
/// column is never blank.
/// </remarks>
public enum DeviceNameStyle
{
    /// <summary>The polling address or DNS name. Always available.</summary>
    Hostname = 0,

    /// <summary>The name the device reports over SNMP (sysName).</summary>
    SysName = 1,

    /// <summary>The display name configured in LibreNMS, if the device has one.</summary>
    DisplayName = 2,
}

public static class DeviceNameStyleExtensions
{
    /// <summary>
    /// Picks the name to show, falling back through sysName to hostname so the
    /// result is never empty.
    /// </summary>
    /// <param name="style">The user's preference.</param>
    /// <param name="device">The cached device, or null if the list has not loaded.</param>
    /// <param name="alertHostname">The hostname the alerts endpoint supplied.</param>
    public static string Resolve(this DeviceNameStyle style, Device? device, string? alertHostname)
    {
        var fallback = FirstNonEmpty(alertHostname, device?.Hostname);

        var preferred = style switch
        {
            DeviceNameStyle.SysName => device?.SysName,
            DeviceNameStyle.DisplayName => FirstNonEmpty(device?.Display, device?.SysName),
            _ => FirstNonEmpty(device?.Hostname, alertHostname),
        };

        return FirstNonEmpty(preferred, fallback) ?? "unknown device";
    }

    /// <summary>
    /// The other name, for showing as a subtitle. Returns null when it would
    /// just repeat <paramref name="primary"/>.
    /// </summary>
    public static string? ResolveSecondary(this DeviceNameStyle style, Device? device, string? alertHostname, string primary)
    {
        var secondary = style switch
        {
            DeviceNameStyle.Hostname => device?.SysName,
            _ => FirstNonEmpty(alertHostname, device?.Hostname),
        };

        return string.IsNullOrWhiteSpace(secondary)
               || string.Equals(secondary, primary, StringComparison.OrdinalIgnoreCase)
            ? null
            : secondary;
    }

    public static string ToDisplayString(this DeviceNameStyle style) => style switch
    {
        DeviceNameStyle.SysName => "sysName",
        DeviceNameStyle.DisplayName => "LibreNMS display name",
        _ => "Hostname",
    };

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

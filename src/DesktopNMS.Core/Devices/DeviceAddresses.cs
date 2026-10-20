using System.Net;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Devices;

/// <summary>
/// Which device an address names - a Graylog message's source, an ARP entry,
/// a name typed in a question. Syslog sources are often a device's sysName or
/// a short hostname rather than what LibreNMS has as its hostname, so exact
/// matches alone left most Graylog messages unmatched (#304).
/// </summary>
public static class DeviceAddresses
{
    /// <summary>
    /// The device whose IP, hostname or sysName is <paramref name="address"/>
    /// (ignoring case), in that order; failing those, the one device whose
    /// short name - the part before the first dot - matches the address's.
    /// A short name shared by two devices matches neither.
    /// </summary>
    public static Device? Find(IEnumerable<Device> devices, string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var wanted = address.Trim();
        var all = devices as IReadOnlyCollection<Device> ?? devices.ToList();

        var exact = all.FirstOrDefault(d => Same(d.Ip, wanted))
                    ?? all.FirstOrDefault(d => Same(d.Hostname, wanted))
                    ?? all.FirstOrDefault(d => Same(d.SysName, wanted));
        if (exact is not null)
        {
            return exact;
        }

        if (ShortName(wanted) is not { } shortName)
        {
            return null;
        }

        Device? found = null;
        foreach (var device in all)
        {
            if (Same(ShortName(device.Hostname), shortName) || Same(ShortName(device.SysName), shortName))
            {
                if (found is not null)
                {
                    return null;
                }

                found = device;
            }
        }

        return found;
    }

    /// <summary>"core-sw1" from "core-sw1.example.net"; null for an IP address or nothing.</summary>
    internal static string? ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (IPAddress.TryParse(trimmed, out _))
        {
            return null;
        }

        var dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);
}

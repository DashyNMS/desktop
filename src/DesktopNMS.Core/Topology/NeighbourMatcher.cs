using System.Net;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Topology;

/// <summary>
/// Finds the monitored device behind an LLDP/CDP neighbour LibreNMS didn't
/// match itself (a link with no <c>remote_device_id</c>). LibreNMS only
/// matches a neighbour's announced name exactly, so an antenna named
/// "00 Stage Left (A)" is never linked to the device "00 stage left a";
/// comparing names without case or punctuation catches those. Only a single,
/// unambiguous match counts - two devices with the same name link neither.
/// </summary>
public static class NeighbourMatcher
{
    /// <summary>Lower case, letters and digits only: "00 Stage Left (A)" and "00 stage left a" are both "00stagelefta".</summary>
    public static string NormaliseName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// The one device whose hostname, sysName or display name matches the
    /// announced name (or its short form, before the first dot - neighbours
    /// often announce "sw1.example.com" for a device called "sw1"), or null
    /// when none or more than one does.
    /// </summary>
    public static int? MatchByName(string? announced, IEnumerable<Device> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var keys = Keys(announced);
        if (keys.Count == 0)
        {
            return null;
        }

        var matches = devices
            .Where(d => Keys(d.Hostname).Concat(Keys(d.SysName)).Concat(Keys(d.Display)).Any(keys.Contains))
            .Select(d => d.DeviceId)
            .Distinct()
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// The MAC address in an announced port id - many devices (wireless antennas
    /// antennas among them) announce their MAC as their port - as 12
    /// lower-case hex digits, or null when the port id isn't a MAC.
    /// </summary>
    public static string? MacFromPortId(string? announcedPort)
    {
        var label = PortLabels.FromNeighbourPort(announcedPort);
        if (label is null || label.Length != 17 || label.Count(c => c == ':') != 5)
        {
            return null;
        }

        var hex = new string(label.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 ? hex.ToLowerInvariant() : null;
    }

    /// <summary>A name's comparable forms - itself, and its short form before the first dot (not for an IP address). Names under 3 characters are too loose to match on.</summary>
    private static HashSet<string> Keys(string? name)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(name))
        {
            return keys;
        }

        var trimmed = name.Trim();
        Add(trimmed);

        var dot = trimmed.IndexOf('.');
        if (dot > 0 && !IPAddress.TryParse(trimmed, out _))
        {
            Add(trimmed[..dot]);
        }

        return keys;

        void Add(string value)
        {
            var key = NormaliseName(value);
            if (key.Length >= 3)
            {
                keys.Add(key);
            }
        }
    }
}

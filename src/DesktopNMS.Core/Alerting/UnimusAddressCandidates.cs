using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Builds the ordered list of addresses to try against Unimus's
/// <c>devices/findByAddress</c> for one LibreNMS device (issue #115) - pure
/// and side-effect free so it can be unit tested directly, the same shape as
/// <see cref="AlertChangeDetector"/>.
/// </summary>
/// <remarks>
/// Mirrors LibreNMS's own server-side Unimus client
/// (<c>App\ApiClients\Unimus::addressCandidates</c>) exactly, since that is
/// the closest thing to a spec for "how does LibreNMS itself do this
/// matching": hostname, hostname with any domain suffix stripped, hostname
/// with LibreNMS's configured discovery domain (<c>mydomain</c>) appended,
/// then the device's own IP. The one difference: LibreNMS's PHP also tries
/// <c>overwrite_ip</c> before <c>ip</c> when an admin has manually overridden
/// a device's address - not modelled on this app's <see cref="Device"/> type,
/// so only the SNMP-resolved <see cref="Device.Ip"/> is used here.
/// </remarks>
public static class UnimusAddressCandidates
{
    /// <summary>
    /// <paramref name="myDomain"/> is LibreNMS's own discovery domain suffix
    /// (its <c>mydomain</c> config) - not exposed via LibreNMS's API, so it
    /// has to come from this app's own Unimus settings
    /// (<see cref="Configuration.UnimusSettings.MyDomain"/>) if it's set on
    /// the LibreNMS side. Candidates are returned in trying order, with
    /// duplicates and blanks removed - the first one Unimus recognises wins.
    /// </summary>
    public static IReadOnlyList<string> ForDevice(Device device, string? myDomain)
    {
        ArgumentNullException.ThrowIfNull(device);

        var candidates = new List<string?>
        {
            device.Hostname,
            StripDomain(device.Hostname),
        };

        if (!string.IsNullOrWhiteSpace(myDomain) && !string.IsNullOrWhiteSpace(device.Hostname))
        {
            // Appends to the raw hostname, not the domain-stripped one above -
            // matches LibreNMS's PHP verbatim ($device->hostname . '.' .
            // $domain), even though that produces an odd double-domain
            // candidate when the hostname already has one. The common case
            // this is actually for is a bare short hostname with no domain.
            candidates.Add($"{device.Hostname}.{myDomain.Trim()}");
        }

        candidates.Add(device.Ip);

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The part of a hostname before its first dot - PHP's <c>strtok($hostname, '.')</c>, verbatim.</summary>
    private static string? StripDomain(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        var dot = hostname.IndexOf('.');
        return dot < 0 ? hostname : hostname[..dot];
    }
}

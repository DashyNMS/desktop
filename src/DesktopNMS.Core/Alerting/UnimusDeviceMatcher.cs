using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Matches a LibreNMS device to one of Unimus's own devices, given the
/// candidate addresses from <see cref="UnimusAddressCandidates"/> and
/// Unimus's full device list - pure and side-effect free so it can be unit
/// tested directly, the same shape as <see cref="AlertChangeDetector"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists instead of calling Unimus's own <c>devices/findByAddress</c>
/// per candidate (what LibreNMS's own server-side Unimus client does)
/// because that endpoint turned out, on live testing against a real
/// multi-zone Unimus instance, not to do what its name and docs suggest:
/// </para>
/// <list type="bullet">
/// <item>It only matches a device's <see cref="UnimusDevice.Address"/> (its
/// IP) - never <see cref="UnimusDevice.Description"/> (Unimus's hostname-ish
/// field). A hostname candidate can never match through it, at all.</item>
/// <item>It is scoped to one Unimus "zone" at a time (a <c>zoneId</c> query
/// parameter, defaulting to the empty "Default Zone" if omitted). A real
/// deployment with devices split across zones - confirmed live: one
/// instance had 232 devices across 4 zones and zero of them in the default
/// one - returns a 404 for every single device unless the caller already
/// knows which zone to ask, which this app has no way to know up front.</item>
/// </list>
/// <para>
/// Fetching the whole device list once (<see cref="Api.IUnimusApi.ListAllDevicesAsync"/>,
/// confirmed to return devices across every zone with no <c>zoneId</c> filter
/// needed) and matching client-side against both fields sidesteps both
/// problems, and is also fewer API calls overall (one paginated fetch versus
/// one <c>findByAddress</c> round trip per candidate per device).
/// </para>
/// </remarks>
public static class UnimusDeviceMatcher
{
    /// <summary>
    /// Returns the first Unimus device whose <see cref="UnimusDevice.Address"/>
    /// or <see cref="UnimusDevice.Description"/> equals one of
    /// <paramref name="candidates"/>, tried in the candidates' own priority
    /// order (each candidate checked against both fields before moving to
    /// the next) - null if none match.
    /// </summary>
    public static UnimusDevice? Match(IReadOnlyList<UnimusDevice> unimusDevices, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(unimusDevices);
        ArgumentNullException.ThrowIfNull(candidates);

        foreach (var candidate in candidates)
        {
            foreach (var device in unimusDevices)
            {
                if (string.Equals(device.Address, candidate, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(device.Description, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return device;
                }
            }
        }

        return null;
    }
}

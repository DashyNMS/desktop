using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Topology;

/// <summary>How a neighbour was tied to a device LibreNMS monitors.</summary>
public enum NeighbourMatchKind
{
    /// <summary>Not a device LibreNMS monitors, as far as can be told.</summary>
    None,

    /// <summary>LibreNMS linked it itself (remote_device_id).</summary>
    LibreNms,

    /// <summary>Matched by its announced name - see <see cref="NeighbourMatcher.MatchByName"/>.</summary>
    Name,

    /// <summary>Matched by the MAC it announces, through an ARP entry for a device's IP.</summary>
    Mac,
}

/// <summary>Which side's discovery found the connection.</summary>
public enum NeighbourSource
{
    /// <summary>This device's own LLDP/CDP sees the neighbour.</summary>
    ThisDevice,

    /// <summary>The neighbour's LLDP/CDP sees this device - how a device that doesn't run LLDP itself (an AP, a phone) still shows its switch.</summary>
    Neighbour,
}

/// <summary>One connection between this device and another, from either side's discovery.</summary>
/// <param name="LocalPortId">This device's port, when known.</param>
/// <param name="LocalPortName">This device's port by name - from its port list, or as the neighbour announced it.</param>
/// <param name="RemoteName">The neighbour as it announced itself (or as LibreNMS names it, seen from its side).</param>
/// <param name="RemotePortId">The neighbour's port, when LibreNMS knows it.</param>
/// <param name="RemotePortName">The neighbour's port by name, as announced (or looked up).</param>
/// <param name="RemoteDeviceId">The neighbour's LibreNMS device, when it's one.</param>
public sealed record DeviceNeighbour(
    NeighbourSource Source,
    int? LocalPortId,
    string? LocalPortName,
    string RemoteName,
    int? RemotePortId,
    string? RemotePortName,
    int? RemoteDeviceId,
    NeighbourMatchKind Match,
    IReadOnlyList<string> Protocols,
    string? Platform,
    bool Active);

/// <summary>
/// Everything a device is connected to, from both directions: the neighbours
/// its own LLDP/CDP reports, and the devices whose LLDP/CDP reports it (a
/// device with no discovery of its own still shows what it's plugged into).
/// A connection both sides report shows once, from this device's side; the
/// same neighbour seen by LLDP and CDP on one port shows once with both.
/// </summary>
public static class DeviceNeighbours
{
    /// <param name="deviceId">This device.</param>
    /// <param name="ownLinks">This device's links (devices/{id}/links).</param>
    /// <param name="fleetLinks">Every device's links (resources/links) - only the ones naming this device as the remote are used.</param>
    /// <param name="portNames">This device's port names by port id.</param>
    /// <param name="matches">For own links LibreNMS didn't link, the device each was matched to, by link id.</param>
    public static IReadOnlyList<DeviceNeighbour> Build(
        int deviceId,
        IEnumerable<NetworkLink> ownLinks,
        IEnumerable<NetworkLink> fleetLinks,
        IReadOnlyDictionary<int, string> portNames,
        IReadOnlyDictionary<int, (int DeviceId, NeighbourMatchKind Kind)> matches)
    {
        ArgumentNullException.ThrowIfNull(ownLinks);
        ArgumentNullException.ThrowIfNull(fleetLinks);
        ArgumentNullException.ThrowIfNull(portNames);
        ArgumentNullException.ThrowIfNull(matches);

        var result = new List<DeviceNeighbour>();

        // This device's own discovery, one row per port and neighbour.
        var own = ownLinks
            .Where(l => l.LocalDeviceId == 0 || l.LocalDeviceId == deviceId)
            .Select(l =>
            {
                int? remoteId = null;
                var kind = NeighbourMatchKind.None;
                if (l.RemoteDeviceId is { } linked && linked > 0 && linked != deviceId)
                {
                    remoteId = linked;
                    kind = NeighbourMatchKind.LibreNms;
                }
                else if (matches.TryGetValue(l.Id, out var m))
                {
                    remoteId = m.DeviceId;
                    kind = m.Kind;
                }

                return (Link: l, RemoteId: remoteId, Kind: kind);
            })
            .GroupBy(x => (x.Link.LocalPortId, Remote: x.RemoteId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? NeighbourMatcher.NormaliseName(x.Link.RemoteHostname)));

        foreach (var group in own)
        {
            var best = group.OrderByDescending(x => x.Link.Active).First();
            var link = best.Link;
            result.Add(new DeviceNeighbour(
                NeighbourSource.ThisDevice,
                link.LocalPortId > 0 ? link.LocalPortId : null,
                link.LocalPortId > 0 && portNames.TryGetValue(link.LocalPortId, out var local) ? local : null,
                link.DisplayRemoteName,
                link.RemotePortId is > 0 ? link.RemotePortId : null,
                link.RemotePort,
                best.RemoteId,
                best.Kind,
                Protocols(group.Select(x => x.Link)),
                FirstText(group.Select(x => x.Link.RemotePlatform)),
                group.Any(x => x.Link.Active)));
        }

        // Other devices' discovery naming this one - unless this device
        // already reports the same connection itself.
        var theirs = fleetLinks
            .Where(l => l.RemoteDeviceId == deviceId && l.LocalDeviceId > 0 && l.LocalDeviceId != deviceId)
            .GroupBy(l => (l.LocalDeviceId, l.LocalPortId));

        foreach (var group in theirs)
        {
            var link = group.OrderByDescending(l => l.Active).First();
            var myPortId = link.RemotePortId is > 0 ? link.RemotePortId : null;

            var alreadyShown = result.Any(r =>
                r.Source == NeighbourSource.ThisDevice
                && r.RemoteDeviceId == link.LocalDeviceId
                && (myPortId is null || r.LocalPortId is null || r.LocalPortId == myPortId)
                && (r.RemotePortId is null || r.RemotePortId == link.LocalPortId));
            if (alreadyShown)
            {
                continue;
            }

            result.Add(new DeviceNeighbour(
                NeighbourSource.Neighbour,
                myPortId,
                myPortId is { } id && portNames.TryGetValue(id, out var mine) ? mine : link.RemotePort,
                $"Device {link.LocalDeviceId}",
                link.LocalPortId > 0 ? link.LocalPortId : null,
                null,
                link.LocalDeviceId,
                NeighbourMatchKind.LibreNms,
                Protocols(group),
                null,
                group.Any(l => l.Active)));
        }

        return result;
    }

    private static IReadOnlyList<string> Protocols(IEnumerable<NetworkLink> links) => links
        .Select(l => l.Protocol?.Trim().ToUpperInvariant())
        .Where(p => !string.IsNullOrEmpty(p))
        .Distinct()
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToList()!;

    private static string? FirstText(IEnumerable<string?> values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Topology;

/// <summary>
/// A connection between two monitored devices on the network map - every
/// discovered link between the same pair folded into one edge, since the map
/// draws one line per pair (thicker for more than one physical link).
/// </summary>
public sealed class TopologyEdge
{
    public TopologyEdge(int deviceA, int deviceB, IReadOnlyList<TopologyConnection> connections)
    {
        DeviceA = deviceA;
        DeviceB = deviceB;
        Connections = connections;
    }

    /// <summary>The lower device id of the pair - edges are undirected, so the pair is always stored in this order.</summary>
    public int DeviceA { get; }

    public int DeviceB { get; }

    /// <summary>One entry per distinct physical connection (port pair) between the two devices.</summary>
    public IReadOnlyList<TopologyConnection> Connections { get; }

    public int LinkCount => Connections.Count;

    public bool Touches(int deviceId) => DeviceA == deviceId || DeviceB == deviceId;

    public int Other(int deviceId) => deviceId == DeviceA ? DeviceB : DeviceA;
}

/// <summary>
/// One physical connection between <see cref="TopologyEdge.DeviceA"/> and
/// <see cref="TopologyEdge.DeviceB"/>, with each end's port name where
/// known. A link record only names the <em>remote</em> port, so each side's
/// name comes from the record discovered on the other device - a cable only
/// reported from one end has just one name.
/// </summary>
public sealed class TopologyConnection
{
    /// <summary>The port on device A (e.g. "Gi1/0/48"), or null if only B reported this cable.</summary>
    public string? PortA { get; internal set; }

    /// <summary>The port on device B, or null if only A reported this cable.</summary>
    public string? PortB { get; internal set; }
}

public sealed class TopologyGraph
{
    /// <summary>Every device id on the map, in ascending order.</summary>
    public required IReadOnlyList<int> DeviceIds { get; init; }

    public required IReadOnlyList<TopologyEdge> Edges { get; init; }

    /// <summary>Devices in scope that have no connection to anything else in scope - listed separately so the map can leave them out, or lay them out apart from the connected graph.</summary>
    public required IReadOnlyList<int> UnlinkedDeviceIds { get; init; }
}

/// <summary>
/// Builds the network map's graph (issue #56) from LibreNMS's fleet-wide
/// LLDP/CDP link list. Only links between two monitored devices both in
/// scope become edges - neighbours LibreNMS doesn't monitor (phones, APs,
/// anything without a <c>remote_device_id</c>) are left out entirely.
/// </summary>
public static class NetworkTopology
{
    /// <param name="scopeDeviceIds">The devices this map covers - the whole fleet, or one device group's members.</param>
    /// <param name="links">LibreNMS's fleet-wide link list.</param>
    /// <param name="portNames">
    /// Port id to name, for every port LibreNMS knows (see
    /// <see cref="PortLabels.ForPort"/>). A link record only names the port at
    /// its far end in text; its own end is just a <c>local_port_id</c>. So a
    /// cable reported from one side only - always the case for a ping-only
    /// device such as a wireless antenna, which LibreNMS never runs discovery
    /// on - has no name for the reporting device's own port unless it's
    /// looked up here. Without it, only the far ends are named.
    /// </param>
    public static TopologyGraph Build(IEnumerable<int> scopeDeviceIds, IEnumerable<NetworkLink> links, IReadOnlyDictionary<int, string>? portNames = null)
    {
        var scope = scopeDeviceIds.ToHashSet();
        var byPair = new Dictionary<(int A, int B), Dictionary<(string, string), TopologyConnection>>();

        foreach (var link in links)
        {
            if (link.RemoteDeviceId is not { } remote
                || link.LocalDeviceId == remote
                || !scope.Contains(link.LocalDeviceId)
                || !scope.Contains(remote))
            {
                continue;
            }

            var pair = link.LocalDeviceId < remote ? (link.LocalDeviceId, remote) : (remote, link.LocalDeviceId);

            if (!byPair.TryGetValue(pair, out var physical))
            {
                physical = new Dictionary<(string, string), TopologyConnection>();
                byPair[pair] = physical;
            }

            // The same cable is normally reported from both ends (A's port ->
            // B's port, and B's port -> A's port), so it's keyed by the two
            // port ids in a fixed order. Without a remote port id, the
            // remote port's name stands in for it. LibreNMS sends a missing
            // remote port id as null, "" or 0 - all mean "not known".
            var remotePortId = link.RemotePortId is > 0 ? link.RemotePortId : null;
            var local = "p" + link.LocalPortId;
            var far = remotePortId is { } rp ? "p" + rp : "n" + link.RemoteDeviceId + ":" + (link.RemotePort ?? string.Empty);
            var key = string.CompareOrdinal(local, far) <= 0 ? (local, far) : (far, local);

            if (!physical.TryGetValue(key, out var connection))
            {
                connection = new TopologyConnection();
                physical[key] = connection;
            }

            // Each end named from its own port where LibreNMS has it, the
            // far end falling back to the name the discovery protocol gave.
            var nearName = PortName(portNames, link.LocalPortId);
            var farName = PortName(portNames, remotePortId) ?? PortLabels.FromNeighbourPort(link.RemotePort);

            if (link.LocalDeviceId == pair.Item1)
            {
                connection.PortA ??= nearName;
                connection.PortB ??= farName;
            }
            else
            {
                connection.PortB ??= nearName;
                connection.PortA ??= farName;
            }
        }

        var edges = byPair
            .OrderBy(kv => kv.Key.A)
            .ThenBy(kv => kv.Key.B)
            .Select(kv => new TopologyEdge(kv.Key.A, kv.Key.B, kv.Value.Values.ToList()))
            .ToList();

        var linked = edges.SelectMany(e => new[] { e.DeviceA, e.DeviceB }).ToHashSet();

        return new TopologyGraph
        {
            DeviceIds = scope.OrderBy(id => id).ToList(),
            Edges = edges,
            UnlinkedDeviceIds = scope.Where(id => !linked.Contains(id)).OrderBy(id => id).ToList(),
        };
    }

    private static string? PortName(IReadOnlyDictionary<int, string>? portNames, int? portId) =>
        portNames is not null && portId is > 0 && portNames.TryGetValue(portId.Value, out var name) ? name : null;
}

/// <summary>How ports are named on the network map.</summary>
public static class PortLabels
{
    private static readonly System.Text.RegularExpressions.Regex MacAddress = new(
        @"^(?<mac>([0-9a-f]{2}[\s:\-]?){5}[0-9a-f]{2})(\s*\([0-9a-f]{12}\))?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A port's short name (ifName, e.g. "Gi1/0/48") - what fits beside a line on a map - or its ifDescr, or null.</summary>
    public static string? ForPort(Port port) =>
        !string.IsNullOrWhiteSpace(port.IfName) ? port.IfName.Trim()
        : !string.IsNullOrWhiteSpace(port.IfDescr) ? port.IfDescr.Trim()
        : null;

    /// <summary>
    /// The port name a neighbour announced over LLDP/CDP, tidied: many
    /// devices (wireless antennas among them) announce their MAC address
    /// as their port, which LibreNMS stores as e.g. "00 19 7C 02 E8 8B
    /// (00197c02e88b)" - shown as "00:19:7C:02:E8:8B". Anything else is kept
    /// as announced; blank is null.
    /// </summary>
    public static string? FromNeighbourPort(string? announced)
    {
        if (string.IsNullOrWhiteSpace(announced))
        {
            return null;
        }

        var text = announced.Trim();
        var match = MacAddress.Match(text);
        if (!match.Success)
        {
            return text;
        }

        var hex = new string(match.Groups["mac"].Value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}

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
    public static TopologyGraph Build(IEnumerable<int> scopeDeviceIds, IEnumerable<NetworkLink> links)
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
            // remote port's name stands in for it.
            var local = "p" + link.LocalPortId;
            var far = link.RemotePortId is { } rp ? "p" + rp : "n" + link.RemoteDeviceId + ":" + (link.RemotePort ?? string.Empty);
            var key = string.CompareOrdinal(local, far) <= 0 ? (local, far) : (far, local);

            if (!physical.TryGetValue(key, out var connection))
            {
                connection = new TopologyConnection();
                physical[key] = connection;
            }

            // This record names the port at its far end.
            if (link.LocalDeviceId == pair.Item1)
            {
                connection.PortB ??= Blank(link.RemotePort);
            }
            else
            {
                connection.PortA ??= Blank(link.RemotePort);
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

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

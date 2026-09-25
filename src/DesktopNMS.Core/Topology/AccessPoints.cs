using System.Text.RegularExpressions;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Topology;

/// <summary>A wireless access point, as a switch sees it over LLDP: its name and model, and the switch port it's plugged into.</summary>
public sealed record AccessPoint(
    string Name,
    string? Model,
    string? Mac,
    int SwitchDeviceId,
    int SwitchPortId,
    bool Active)
{
    /// <summary>True when the AP doesn't announce a name of its own over LLDP - <see cref="Name"/> is then <see cref="AccessPoints.UnknownName"/>, and its MAC tells it apart.</summary>
    public bool IsUnnamed { get; init; }
}

/// <summary>
/// Finds the access points in LibreNMS's LLDP/CDP neighbour links (#55).
/// LibreNMS keeps each AP's clients, channel and radio use in its own
/// access_points table, but no API route returns them - the switch side
/// is what the API does have: every AP announces itself to the switch
/// port it's plugged into, so the links list names each AP, its model and
/// where it's connected. Recognised by what the AP announces as its
/// software: Aruba APs send "ArubaOS (MODEL: 535), Version Aruba AP"
/// (checked live against a fleet of AP-345, AP-535 and AP-567s).
/// </summary>
public static class AccessPoints
{
    private static readonly Regex[] Signatures =
    [
        new(@"\bAruba AP\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>What an AP that announces no name of its own is called - its MAC, shown alongside, says which one it is.</summary>
    public const string UnknownName = "Unknown AP";

    private static readonly Regex ModelPattern = new(@"MODEL:\s*([^)]+?)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsAccessPoint(NetworkLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.RemoteVersion is { } version && Signatures.Any(s => s.IsMatch(version));
    }

    /// <summary>"535" -> "AP-535"; a model that already names itself is kept as it is.</summary>
    public static string? ModelOf(string? version)
    {
        if (version is null || ModelPattern.Match(version) is not { Success: true } match)
        {
            return null;
        }

        var model = match.Groups[1].Value.Trim();
        return model.Length == 0 ? null : model.All(char.IsLetter) || model.Contains('-', StringComparison.Ordinal) ? model : "AP-" + model;
    }

    /// <summary>
    /// A map node id for an AP: negative, so it can never collide with a
    /// LibreNMS device id, and stable across sessions (the network map
    /// remembers node positions by id) - a hash of its name, or of its MAC
    /// for one without a name. An AP on two switch ports is one node.
    /// </summary>
    public static int NodeId(AccessPoint accessPoint)
    {
        ArgumentNullException.ThrowIfNull(accessPoint);

        var key = "ap:" + (accessPoint.IsUnnamed ? accessPoint.Mac ?? accessPoint.Name : accessPoint.Name).ToLowerInvariant();

        // FNV-1a - string.GetHashCode is randomised per process.
        var hash = 2166136261u;
        foreach (var c in key)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return -1 - (int)(hash & 0x3FFFFFFF);
    }

    /// <summary>
    /// The network map's line for each AP-to-switch cable: from the AP's
    /// node (<see cref="NodeId"/>) to its switch, named at the switch end
    /// from <paramref name="portNames"/>. The AP end is its uplink - Aruba
    /// APs announce their MAC there rather than a port name.
    /// </summary>
    public static IReadOnlyList<TopologyEdge> MapEdges(IEnumerable<AccessPoint> accessPoints, IReadOnlyDictionary<int, string>? portNames)
    {
        ArgumentNullException.ThrowIfNull(accessPoints);

        return accessPoints
            .GroupBy(ap => (Node: NodeId(ap), ap.SwitchDeviceId))
            .Select(g => new TopologyEdge(
                g.Key.Node,
                g.Key.SwitchDeviceId,
                g.Select(ap => new TopologyConnection
                {
                    PortA = "Uplink",
                    PortB = portNames is not null && portNames.TryGetValue(ap.SwitchPortId, out var name) ? name : null,
                }).ToList()))
            .ToList();
    }

    /// <summary>
    /// Every access point in <paramref name="links"/>, one per link - an AP
    /// seen on two switch ports (a second uplink, or an old link LibreNMS
    /// hasn't aged out yet) is listed on both. Ordered by name.
    /// </summary>
    public static IReadOnlyList<AccessPoint> FromLinks(IEnumerable<NetworkLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        return links
            .Where(IsAccessPoint)
            .Select(link =>
            {
                var mac = PortLabels.FromNeighbourPort(link.RemotePort);
                var isMac = NeighbourMatcher.MacFromPortId(link.RemotePort) is not null;
                var name = link.RemoteHostname?.Trim();

                // An AP with no name set announces its software string in
                // place of one - its MAC says which AP it is instead.
                var unnamed = string.IsNullOrEmpty(name) || string.Equals(name, link.RemoteVersion?.Trim(), StringComparison.OrdinalIgnoreCase);
                if (unnamed)
                {
                    name = UnknownName;
                }

                return new AccessPoint(name!, ModelOf(link.RemoteVersion), isMac ? mac : null, link.LocalDeviceId, link.LocalPortId, link.Active)
                {
                    IsUnnamed = unnamed,
                };
            })
            .OrderBy(ap => ap.IsUnnamed)
            .ThenBy(ap => ap.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(ap => ap.Mac, StringComparer.Ordinal)
            .ToList();
    }
}

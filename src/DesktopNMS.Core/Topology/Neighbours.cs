using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Topology;

/// <summary>
/// Something a switch sees on one of its ports over LLDP/CDP, as LibreNMS
/// keeps it: the name and description it announces, its port (often its
/// MAC), and the switch port it's plugged into - plus the LibreNMS device
/// it is, when LibreNMS monitors it too.
/// </summary>
public sealed record Neighbour(
    string Name,
    string? Description,
    string? RemotePort,
    string? Mac,
    string? Protocol,
    int SwitchDeviceId,
    int SwitchPortId,
    int? RemoteDeviceId,
    bool Active)
{
    /// <summary>It announces no name of its own - <see cref="Name"/> is then <see cref="Neighbours.UnnamedName"/>, and its MAC or port tells it apart.</summary>
    public bool IsUnnamed { get; init; }

    /// <summary>The LLDP SysName exactly as announced - what a System name rule tests, even for an unnamed one.</summary>
    public string? AnnouncedName { get; init; }

    public bool IsMonitored => RemoteDeviceId is > 0;
}

/// <summary>
/// The Neighbours tab's view rules (#55): which of the neighbours LibreNMS's
/// links list shows up in a user's view - see <see cref="NeighbourViewDefinition"/>.
/// Nothing here knows about any vendor; a view says what to look for.
/// </summary>
public static class Neighbours
{
    public const string UnnamedName = "Unnamed";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    /// <summary>Every neighbour in the links list, one per link, ordered by name (unnamed ones last).</summary>
    public static IReadOnlyList<Neighbour> FromLinks(IEnumerable<NetworkLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        return links
            .Select(link =>
            {
                var announced = link.RemoteHostname?.Trim();
                var description = string.IsNullOrWhiteSpace(link.RemoteVersion) ? null : link.RemoteVersion.Trim();

                // Some kit with no name set announces its description in
                // place of one - that's no name either.
                var unnamed = string.IsNullOrEmpty(announced) || string.Equals(announced, description, StringComparison.OrdinalIgnoreCase);
                var mac = NeighbourMatcher.MacFromPortId(link.RemotePort) is not null ? PortLabels.FromNeighbourPort(link.RemotePort) : null;

                return new Neighbour(
                    unnamed ? UnnamedName : announced!,
                    description,
                    PortLabels.FromNeighbourPort(link.RemotePort),
                    mac,
                    link.Protocol,
                    link.LocalDeviceId,
                    link.LocalPortId,
                    link.RemoteDeviceId is > 0 ? link.RemoteDeviceId : null,
                    link.Active)
                {
                    IsUnnamed = unnamed,
                    AnnouncedName = announced,
                };
            })
            .OrderBy(n => n.IsUnnamed)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.RemotePort, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether <paramref name="neighbour"/> belongs in <paramref name="view"/>.
    /// A view with no rules (or none with a value) matches nothing - listing
    /// every neighbour of every switch is never what anyone wants.
    /// </summary>
    public static bool Matches(NeighbourViewDefinition view, Neighbour neighbour, string? switchName, string? switchPortDescription)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(neighbour);

        var rules = view.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (rules.Count == 0)
        {
            return false;
        }

        bool Test(NeighbourRule rule) => RuleMatches(rule, rule.Field switch
        {
            NeighbourRuleField.SystemName => neighbour.AnnouncedName,
            NeighbourRuleField.SystemDescription => neighbour.Description,
            NeighbourRuleField.PortId => neighbour.RemotePort,
            NeighbourRuleField.Protocol => neighbour.Protocol,
            NeighbourRuleField.Switch => switchName,
            NeighbourRuleField.SwitchPortDescription => switchPortDescription,
            _ => null,
        });

        return view.MatchAll ? rules.All(Test) : rules.Any(Test);
    }

    /// <summary>One rule against one value, case-insensitive. A missing value only satisfies "does not contain".</summary>
    public static bool RuleMatches(NeighbourRule rule, string? value)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var wanted = rule.Value.Trim();
        var text = value ?? string.Empty;

        return rule.Operator switch
        {
            NeighbourRuleOperator.Contains => text.Contains(wanted, StringComparison.OrdinalIgnoreCase),
            NeighbourRuleOperator.StartsWith => text.StartsWith(wanted, StringComparison.OrdinalIgnoreCase),
            NeighbourRuleOperator.Equals => string.Equals(text.Trim(), wanted, StringComparison.OrdinalIgnoreCase),
            NeighbourRuleOperator.DoesNotContain => !text.Contains(wanted, StringComparison.OrdinalIgnoreCase),
            NeighbourRuleOperator.Matches => RegexFor(wanted) is { } regex && SafeIsMatch(regex, text),
            _ => false,
        };
    }

    /// <summary>Why a regex rule's value won't work, or null if it's fine - for the view editor.</summary>
    public static string? RegexProblem(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// A map node id for a neighbour that isn't a LibreNMS device: negative,
    /// so it never collides with a device id, and stable across sessions
    /// (the network map remembers positions by id) - a hash of its name, or
    /// of its MAC/port for an unnamed one. The same neighbour on two switch
    /// ports is one node.
    /// </summary>
    public static int NodeId(Neighbour neighbour)
    {
        ArgumentNullException.ThrowIfNull(neighbour);

        var key = "nb:" + (neighbour.IsUnnamed ? neighbour.Mac ?? neighbour.RemotePort ?? neighbour.Name : neighbour.Name).ToLowerInvariant();

        // FNV-1a - string.GetHashCode is randomised per process.
        var hash = 2166136261u;
        foreach (var c in key)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return -1 - (int)(hash & 0x3FFFFFFF);
    }

    /// <summary>
    /// The network map's line for each neighbour-to-switch cable: from the
    /// neighbour's node (<see cref="NodeId"/>) to its switch, named at the
    /// switch end from <paramref name="portNames"/> and at the neighbour's
    /// end from the port it announces.
    /// </summary>
    public static IReadOnlyList<TopologyEdge> MapEdges(IEnumerable<Neighbour> neighbours, IReadOnlyDictionary<int, string>? portNames)
    {
        ArgumentNullException.ThrowIfNull(neighbours);

        return neighbours
            .GroupBy(n => (Node: NodeId(n), n.SwitchDeviceId))
            .Select(g => new TopologyEdge(
                g.Key.Node,
                g.Key.SwitchDeviceId,
                g.Select(n => new TopologyConnection
                {
                    PortA = n.RemotePort,
                    PortB = portNames is not null && portNames.TryGetValue(n.SwitchPortId, out var name) ? name : null,
                }).ToList()))
            .ToList();
    }

    private static Regex? RegexFor(string pattern) => RegexCache.GetOrAdd(pattern, p =>
    {
        try
        {
            return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    });

    private static bool SafeIsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

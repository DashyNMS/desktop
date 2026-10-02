using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Topology;

/// <summary>A VLAN's ports on one device, split by how they carry it.</summary>
public sealed record VlanPorts(IReadOnlyList<Port> Untagged, IReadOnlyList<Port> Tagged);

/// <summary>
/// Works out which of a device's ports carry a VLAN (#99). LibreNMS's
/// per-port VLAN memberships (<see cref="Port.Vlans"/>) are the truth when
/// the device reports them: untagged for access/native ports, tagged for
/// trunks. A port's <see cref="Port.IfVlan"/> isn't used alongside them -
/// switches report it for trunk ports too (a ProCurve trunk says VLAN 1),
/// which would list a trunk as an access port of a VLAN it may not even
/// carry untagged. Only a device reporting no memberships at all (or a
/// LibreNMS too old to send them) falls back to IfVlan, as the untagged
/// ports - there's no tagged view then.
/// </summary>
public static class VlanMembership
{
    /// <summary>Whether any of the device's ports report VLAN memberships.</summary>
    public static bool HasMembershipData(IEnumerable<Port> ports) => ports.Any(p => p.Vlans.Count > 0);

    public static VlanPorts For(int vlan, IReadOnlyCollection<Port> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        if (!HasMembershipData(ports))
        {
            return new VlanPorts(ports.Where(p => p.IfVlan == vlan).ToList(), Array.Empty<Port>());
        }

        var untagged = new List<Port>();
        var tagged = new List<Port>();

        foreach (var port in ports)
        {
            // A port lists a VLAN once; if a device repeats it, untagged wins.
            var memberships = port.Vlans.Where(m => m.Vlan == vlan).ToList();
            if (memberships.Count == 0)
            {
                continue;
            }

            if (memberships.Any(m => m.Untagged))
            {
                untagged.Add(port);
            }
            else
            {
                tagged.Add(port);
            }
        }

        return new VlanPorts(untagged, tagged);
    }
}

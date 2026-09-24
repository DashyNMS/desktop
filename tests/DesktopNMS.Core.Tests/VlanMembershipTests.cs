using System.Text.Json;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class VlanMembershipTests
{
    private static Port Port(string name, int? ifVlan, params (int Vlan, bool Untagged)[] vlans) => new()
    {
        IfName = name,
        IfVlan = ifVlan,
        Vlans = vlans.Select(v => new PortVlanMembership { Vlan = v.Vlan, Untagged = v.Untagged }).ToList(),
    };

    [Fact]
    public void Memberships_split_a_VLAN_into_untagged_and_tagged_ports()
    {
        var ports = new[]
        {
            Port("1", 2074, (2074, true)),
            Port("2", 2074, (2074, true)),
            Port("A1", 1, (2074, false), (2102, false)),
            Port("3", 2102, (2102, true)),
        };

        var vlan = VlanMembership.For(2074, ports);

        Assert.Equal(new[] { "1", "2" }, vlan.Untagged.Select(p => p.IfName));
        Assert.Equal(new[] { "A1" }, vlan.Tagged.Select(p => p.IfName));
    }

    [Fact]
    public void A_trunks_reported_native_VLAN_is_not_taken_as_access_membership_when_memberships_exist()
    {
        // A ProCurve trunk reports ifVlan 1 but carries VLAN 1 neither
        // untagged nor tagged in its memberships.
        var ports = new[] { Port("A1", 1, (2074, false)), Port("5", 1, (1, true)) };

        var vlan = VlanMembership.For(1, ports);

        Assert.Equal(new[] { "5" }, vlan.Untagged.Select(p => p.IfName));
        Assert.Empty(vlan.Tagged);
    }

    [Fact]
    public void Without_any_memberships_it_falls_back_to_each_ports_own_VLAN()
    {
        var ports = new[] { Port("1", 10), Port("2", 20), Port("3", 10) };

        var vlan = VlanMembership.For(10, ports);

        Assert.Equal(new[] { "1", "3" }, vlan.Untagged.Select(p => p.IfName));
        Assert.Empty(vlan.Tagged);
        Assert.False(VlanMembership.HasMembershipData(ports));
    }

    [Fact]
    public void Untagged_wins_if_a_device_lists_the_same_VLAN_twice()
    {
        var vlan = VlanMembership.For(5, new[] { Port("1", 5, (5, false), (5, true)) });

        Assert.Single(vlan.Untagged);
        Assert.Empty(vlan.Tagged);
    }

    [Fact]
    public void Ports_parse_with_their_VLAN_memberships()
    {
        const string json = """
            [{"port_id":73,"ifName":"1","ifVlan":"2074","vlans":[{"port_vlan_id":437,"device_id":3,"port_id":73,"vlan":2074,"baseport":1,"priority":0,"state":"unknown","cost":0,"untagged":1}]},
             {"port_id":99,"ifName":"A1","ifVlan":"1","vlans":[{"vlan":2102,"untagged":0},{"vlan":"2074","untagged":"0"}]},
             {"port_id":100,"ifName":"B1"}]
            """;

        var ports = JsonSerializer.Deserialize<List<Port>>(json, LibreNmsJson.Options)!;

        Assert.True(Assert.Single(ports[0].Vlans).Untagged);
        Assert.Equal(new[] { 2102, 2074 }, ports[1].Vlans.Select(v => v.Vlan));
        Assert.All(ports[1].Vlans, v => Assert.False(v.Untagged));
        Assert.Empty(ports[2].Vlans);
    }
}

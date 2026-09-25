using System.Text.Json;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class NeighboursTests
{
    private const string Bolero = "Riedel Bolero DECT Antenna G2 3.5.0-12";
    private const string ArubaAp = "ArubaOS (MODEL: 535), Version Aruba AP";

    private static NetworkLink L(string? name, string? version, int device = 1, int port = 10, string? remotePort = "00 19 7C 02 59 FC (00197c0259fc)", int? remoteDevice = null) =>
        new() { RemoteHostname = name, RemoteVersion = version, LocalDeviceId = device, LocalPortId = port, RemotePort = remotePort, RemoteDeviceId = remoteDevice, Protocol = "lldp" };

    private static NeighbourViewDefinition View(bool matchAll, params (NeighbourRuleField Field, NeighbourRuleOperator Op, string Value)[] rules) => new()
    {
        MatchAll = matchAll,
        Rules = rules.Select(r => new NeighbourRule { Field = r.Field, Operator = r.Op, Value = r.Value }).ToList(),
    };

    [Fact]
    public void A_neighbour_carries_what_LibreNMS_keeps_from_LLDP()
    {
        var n = Assert.Single(Neighbours.FromLinks(new[] { L("FOM Broadcast Center 02", Bolero, remoteDevice: 336) }));

        Assert.Equal("FOM Broadcast Center 02", n.Name);
        Assert.Equal(Bolero, n.Description);
        Assert.Equal("00:19:7C:02:59:FC", n.Mac);
        Assert.Equal("lldp", n.Protocol);
        Assert.Equal(336, n.RemoteDeviceId);
        Assert.True(n.IsMonitored);
    }

    [Fact]
    public void One_that_announces_its_description_as_its_name_is_unnamed_listed_last()
    {
        var list = Neighbours.FromLinks(new[] { L(ArubaAp, ArubaAp), L("r-ap-zz-01", ArubaAp), L(null, "x") });

        Assert.Equal(new[] { "r-ap-zz-01", Neighbours.UnnamedName, Neighbours.UnnamedName }, list.Select(n => n.Name));
        Assert.True(list[1].IsUnnamed);
        Assert.Equal(ArubaAp, list[1].AnnouncedName);
    }

    [Theory]
    [InlineData(NeighbourRuleOperator.Contains, "bolero dect", true)]
    [InlineData(NeighbourRuleOperator.StartsWith, "Riedel", true)]
    [InlineData(NeighbourRuleOperator.StartsWith, "Bolero", false)]
    [InlineData(NeighbourRuleOperator.Equals, "riedel bolero dect antenna g2 3.5.0-12", true)]
    [InlineData(NeighbourRuleOperator.DoesNotContain, "Aruba", true)]
    [InlineData(NeighbourRuleOperator.Matches, @"Antenna G\d", true)]
    [InlineData(NeighbourRuleOperator.Matches, @"(unclosed", false)]
    public void Rules_compare_case_insensitively(NeighbourRuleOperator op, string value, bool expected)
    {
        Assert.Equal(expected, Neighbours.RuleMatches(new NeighbourRule { Operator = op, Value = value }, Bolero));
    }

    [Fact]
    public void All_or_any_decides_how_rules_combine()
    {
        var n = Neighbours.FromLinks(new[] { L("FOM Broadcast Center 02", Bolero) })[0];
        var both = (NeighbourRuleField.SystemDescription, NeighbourRuleOperator.Contains, "Bolero");
        var wrongSwitch = (NeighbourRuleField.Switch, NeighbourRuleOperator.StartsWith, "l-sw");

        Assert.False(Neighbours.Matches(View(true, both, wrongSwitch), n, "r-sw-fom-01", "AP"));
        Assert.True(Neighbours.Matches(View(false, both, wrongSwitch), n, "r-sw-fom-01", "AP"));
    }

    [Fact]
    public void The_switch_side_can_be_matched_too()
    {
        var n = Neighbours.FromLinks(new[] { L("x", "y") })[0];

        Assert.True(Neighbours.Matches(View(true, (NeighbourRuleField.SwitchPortDescription, NeighbourRuleOperator.Equals, "AP")), n, "r-sw-1", "AP"));
        Assert.True(Neighbours.Matches(View(true, (NeighbourRuleField.Switch, NeighbourRuleOperator.Contains, "fom")), n, "r-sw-fom-01", null));
    }

    [Fact]
    public void A_view_without_usable_rules_matches_nothing()
    {
        var n = Neighbours.FromLinks(new[] { L("x", Bolero) })[0];

        Assert.False(Neighbours.Matches(new NeighbourViewDefinition(), n, null, null));
        Assert.False(Neighbours.Matches(View(true, (NeighbourRuleField.SystemName, NeighbourRuleOperator.Contains, "  ")), n, null, null));
    }

    [Fact]
    public void Regex_problems_are_reported_for_the_editor()
    {
        Assert.Null(Neighbours.RegexProblem(@"^AP-\d+$"));
        Assert.NotNull(Neighbours.RegexProblem("(unclosed"));
    }

    [Fact]
    public void Map_node_ids_are_negative_stable_and_one_per_neighbour()
    {
        var list = Neighbours.FromLinks(new[] { L("r-ap-it-01", ArubaAp, 1, 10), L("R-AP-IT-01", ArubaAp, 2, 20), L("r-ap-it-02", ArubaAp) });

        Assert.True(Neighbours.NodeId(list[0]) < 0);
        Assert.Equal(Neighbours.NodeId(list[0]), Neighbours.NodeId(list[1]));
        Assert.NotEqual(Neighbours.NodeId(list[0]), Neighbours.NodeId(list[2]));
    }

    [Fact]
    public void Map_edges_join_each_neighbour_to_its_switch()
    {
        var n = Neighbours.FromLinks(new[] { L("r-ap-it-01", ArubaAp, 7, 70) })[0];

        var edge = Assert.Single(Neighbours.MapEdges(new[] { n }, new Dictionary<int, string> { [70] = "Gi1/0/5" }));

        Assert.Equal(Neighbours.NodeId(n), edge.DeviceA);
        Assert.Equal(7, edge.DeviceB);
        Assert.Equal("Gi1/0/5", Assert.Single(edge.Connections).PortB);
    }

    [Fact]
    public void A_link_parses_its_version_and_whether_it_is_active()
    {
        const string json = """{"id":1,"local_port_id":6730,"local_device_id":178,"remote_port_id":0,"active":0,"protocol":"lldp","remote_hostname":"r-ap-fer-pdc-02","remote_device_id":0,"remote_port":"00 4E 35 C5 7B 58 (004e35c57b58)","remote_platform":"","remote_version":"ArubaOS (MODEL: 345), Version Aruba AP"}""";

        var link = JsonSerializer.Deserialize<NetworkLink>(json, LibreNmsJson.Options)!;
        var n = Neighbours.FromLinks(new[] { link })[0];

        Assert.False(n.Active);
        Assert.False(n.IsMonitored);
    }

    [Fact]
    public void View_definitions_survive_a_settings_round_trip()
    {
        var settings = new AppSettings { NeighbourViews = { View(false, (NeighbourRuleField.SystemDescription, NeighbourRuleOperator.Matches, "Bolero")) } };
        settings.NeighbourViews[0].Name = "Bolero antennas";

        var clone = settings.Clone();
        clone.Normalise();

        var view = Assert.Single(clone.NeighbourViews);
        Assert.Equal("Bolero antennas", view.Name);
        Assert.False(view.MatchAll);
        Assert.Equal(NeighbourRuleOperator.Matches, Assert.Single(view.Rules).Operator);
    }
}

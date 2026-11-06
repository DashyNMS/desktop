using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// The Ports tab's cards (#569): the status rule they share with the table,
/// the facts each card shows, and the groups they sit in.
/// </summary>
public sealed class PortCardsTests
{
    [Fact]
    public void An_up_port_with_no_errors_is_green_up()
    {
        var port = Item(Up());

        Assert.Equal("Up", port.StatusLabel);
        Assert.Equal(AlertSeverity.Ok, port.StatusTone);
        Assert.Equal("0", port.ErrorsInText);
        Assert.Equal("0", port.ErrorsOutText);
        Assert.False(port.HasErrorsIn);
        Assert.False(port.HasErrorsOut);
    }

    [Theory]
    [InlineData(3L, 0L, true, false)]
    [InlineData(0L, 2L, false, true)]
    public void Errors_on_either_side_make_an_up_port_red(long errorsIn, long errorsOut, bool inRed, bool outRed)
    {
        var raw = Up();
        raw.IfInErrorsDelta = errorsIn;
        raw.IfOutErrorsDelta = errorsOut;
        var port = Item(raw);

        Assert.Equal("Up · errors", port.StatusLabel);
        Assert.Equal(AlertSeverity.Critical, port.StatusTone);
        Assert.Equal(inRed, port.HasErrorsIn);
        Assert.Equal(outRed, port.HasErrorsOut);
        Assert.Equal(errorsIn.ToString(), port.ErrorsInText);
        Assert.Equal(errorsOut.ToString(), port.ErrorsOutText);
    }

    [Fact]
    public void A_tiny_error_rate_keeps_its_precision()
    {
        var raw = Up();
        raw.IfInErrorsRate = 0.001;
        var port = Item(raw);

        Assert.Equal("0.001/s", port.ErrorsInText);
        Assert.Equal("Up · errors", port.StatusLabel);
    }

    [Theory]
    [InlineData("down")]
    [InlineData("lowerLayerDown")]
    [InlineData("notPresent")]
    public void An_enabled_port_that_is_down_is_amber(string operStatus)
    {
        var raw = Up();
        raw.IfOperStatus = operStatus;
        var port = Item(raw);

        Assert.Equal("Down", port.StatusLabel);
        Assert.Equal(AlertSeverity.Warning, port.StatusTone);
    }

    [Fact]
    public void A_shut_down_port_is_grey_disabled_even_with_errors()
    {
        var raw = Up();
        raw.IfAdminStatus = "down";
        raw.IfOperStatus = "down";
        raw.IfInErrorsDelta = 5;
        var port = Item(raw);

        Assert.Equal("Disabled", port.StatusLabel);
        Assert.Equal(AlertSeverity.Unknown, port.StatusTone);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_port_LibreNMS_does_not_poll_or_ignores_is_grey_not_monitored(bool disabled, bool ignore)
    {
        var raw = Up();
        raw.Disabled = disabled;
        raw.Ignore = ignore;
        var port = Item(raw);

        Assert.Equal("Not monitored", port.StatusLabel);
        Assert.Equal(AlertSeverity.Unknown, port.StatusTone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("testing")]
    [InlineData("dormant")]
    public void Any_other_status_is_grey_unknown(string? operStatus)
    {
        var raw = Up();
        raw.IfOperStatus = operStatus;
        var port = Item(raw);

        Assert.Equal("Unknown", port.StatusLabel);
        Assert.Equal(AlertSeverity.Unknown, port.StatusTone);
    }

    [Fact]
    public void An_up_port_without_error_readings_stays_neutral()
    {
        var raw = Up();
        raw.IfOutErrorsDelta = null;
        var port = Item(raw);

        Assert.Equal("Up · readings unavailable", port.StatusLabel);
        Assert.Equal(AlertSeverity.Unknown, port.StatusTone);
        Assert.Equal("Unavailable", port.ErrorsOutText);
    }

    [Fact]
    public void Missing_readings_say_unavailable_rather_than_zero()
    {
        var raw = Up();
        raw.IfSpeed = null;
        raw.IfInOctetsRate = null;
        raw.IfOutOctetsRate = double.NaN;
        var port = Item(raw);

        Assert.Equal("Speed unavailable", port.CardSpeedText);
        Assert.Equal("Unavailable", port.TrafficInText);
        Assert.Equal("Unavailable", port.TrafficOutText);
    }

    [Fact]
    public void Traffic_is_shown_in_bits_while_up_and_not_at_all_otherwise()
    {
        var raw = Up();
        raw.IfInOctetsRate = 125_000;
        raw.IfOutOctetsRate = 0;
        var up = Item(raw);

        Assert.Equal("1 Mbps", up.TrafficInText);
        Assert.Equal("0 bps", up.TrafficOutText);
        Assert.Equal("1 Gbps", up.CardSpeedText);

        raw.IfOperStatus = "down";
        var down = Item(raw);

        Assert.Equal("Not shown", down.TrafficInText);
        Assert.Equal("Not shown", down.TrafficOutText);
    }

    [Fact]
    public void Vlans_come_from_memberships_unique_and_in_order()
    {
        var raw = Up();
        raw.IfVlan = 1;
        raw.Vlans = [Member(30), Member(10, untagged: true), Member(30), Member(20), Member(0)];

        Assert.Equal("10, 20, 30", Item(raw, deviceReportsVlanMemberships: true).VlansText);
    }

    [Fact]
    public void Without_memberships_on_the_device_the_direct_vlan_is_the_access_or_native_one()
    {
        var raw = Up();
        raw.IfVlan = 42;

        Assert.Equal("42 (access/native VLAN)", Item(raw, deviceReportsVlanMemberships: false).VlansText);
    }

    [Fact]
    public void A_port_without_memberships_on_a_device_with_them_is_not_reported()
    {
        // A trunk can report ifVlan 1, so it isn't trusted once memberships exist.
        var raw = Up();
        raw.IfVlan = 1;

        Assert.Equal("Not reported", Item(raw, deviceReportsVlanMemberships: true).VlansText);
    }

    [Fact]
    public void A_port_with_no_vlan_at_all_is_not_reported()
    {
        Assert.Equal("Not reported", Item(Up()).VlansText);
    }

    [Fact]
    public void A_card_speaks_its_visible_facts_in_reading_order()
    {
        var raw = Up();
        raw.IfAlias = "uplink to core.example.net";
        raw.IfInOctetsRate = 125_000;
        raw.IfOutOctetsRate = 250_000;
        raw.Vlans = [Member(10)];

        Assert.Equal(
            "Gi0/1, uplink to core.example.net, Up, 1 Gbps, traffic in 1 Mbps, out 2 Mbps, errors in 0, out 0, VLANs 10",
            Item(raw, deviceReportsVlanMemberships: true).CardAutomationName);
    }

    [Fact]
    public void Ungrouped_cards_are_one_group_without_a_heading_in_the_given_order()
    {
        var ports = new[] { Item(Up(3)), Item(Up(1)), Item(Up(2)) };

        var groups = PortCardGrouping.Build(ports, PortGrouping.None, _ => { });

        var group = Assert.Single(groups);
        Assert.False(group.HasHeader);
        Assert.Equal(new[] { 3, 1, 2 }, group.Cards.Select(c => c.Port.Model.PortId));
    }

    [Fact]
    public void Grouped_cards_follow_the_tables_group_order_and_keep_the_view_order_inside()
    {
        var down = Up(2);
        down.IfOperStatus = "down";
        var unknown = Up(4);
        unknown.IfOperStatus = null;
        var ports = new[] { Item(down), Item(Up(5)), Item(unknown), Item(Up(1)) };

        var groups = PortCardGrouping.Build(ports, PortGrouping.Status, _ => { });

        Assert.Equal(new[] { "Up", "Down", "Unknown status" }, groups.Select(g => g.Header));
        Assert.Equal(new[] { 5, 1 }, groups[0].Cards.Select(c => c.Port.Model.PortId));
        Assert.Equal("2 ports", groups[0].CountText);
        Assert.Equal("1 port", groups[1].CountText);
    }

    [Fact]
    public void Cards_grouped_by_vlan_order_by_number_with_no_vlan_last()
    {
        var a = Up(1);
        a.IfVlan = 100;
        var b = Up(2);
        var c = Up(3);
        c.IfVlan = 20;

        var groups = PortCardGrouping.Build(new[] { Item(a), Item(b), Item(c) }, PortGrouping.Vlan, _ => { });

        Assert.Equal(new[] { "VLAN 20", "VLAN 100", "No VLAN" }, groups.Select(g => g.Header));
    }

    [Fact]
    public void No_ports_make_no_groups()
    {
        Assert.Empty(PortCardGrouping.Build(Array.Empty<PortItemViewModel>(), PortGrouping.Status, _ => { }));
    }

    [Fact]
    public void Picking_a_card_hands_its_port_on()
    {
        var port = Item(Up());
        PortItemViewModel? picked = null;
        var card = PortCardGrouping.Build(new[] { port }, PortGrouping.None, p => picked = p)[0].Cards[0];

        card.SelectCommand.Execute(null);

        Assert.Same(port, picked);
    }

    private static Port Up(int id = 1) => new()
    {
        PortId = id,
        DeviceId = 7,
        IfName = "Gi0/" + id,
        IfDescr = "Gi0/" + id,
        IfOperStatus = "up",
        IfAdminStatus = "up",
        IfSpeed = 1_000_000_000,
        IfInErrorsDelta = 0,
        IfOutErrorsDelta = 0,
    };

    private static PortVlanMembership Member(int vlan, bool untagged = false) => new() { Vlan = vlan, Untagged = untagged };

    private static PortItemViewModel Item(Port port, bool deviceReportsVlanMemberships = false) =>
        new(port, null, Array.Empty<DeviceIpAddress>(), _ => { }, null, deviceReportsVlanMemberships);
}

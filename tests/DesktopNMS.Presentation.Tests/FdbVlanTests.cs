using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// The MAC table's VLAN column on LibreNMS 26.11 and older servers (#587),
/// and rules flagged for the columns the connected server removed.
/// </summary>
public sealed class FdbVlanTests
{
    private static readonly Vlan[] Vlans =
    [
        new() { VlanId = 50, DeviceId = 1, VlanNumber = 2074, VlanName = "Cameras" },
        new() { VlanId = 51, DeviceId = 1, VlanNumber = 2099, VlanName = "Voice" },
    ];

    private static FdbItemViewModel Row(FdbEntry entry, FdbVlanLookup? lookup = null)
        => new(entry, new Dictionary<int, string>(), lookup ?? new FdbVlanLookup { Resolver = new FdbVlanResolver(Vlans) }, serverTimestampsAreUtc: false);

    [Fact]
    public void A_26_11_row_shows_its_number_and_the_name_on_that_device()
    {
        var row = Row(new FdbEntry { DeviceId = 1, VlanNumber = 2099 });

        Assert.Equal("2099", row.VlanText);
        Assert.Equal("Voice", row.VlanNameText);
    }

    [Fact]
    public void A_26_11_number_shows_before_the_vlan_table_loads()
    {
        var row = Row(new FdbEntry { DeviceId = 1, VlanNumber = 3010 }, new FdbVlanLookup());

        Assert.Equal("3010", row.VlanText);
        Assert.Null(row.VlanNameText);
    }

    [Fact]
    public void An_older_row_resolves_its_internal_id()
    {
        var row = Row(new FdbEntry { DeviceId = 1, VlanId = 50 });

        Assert.Equal("2074", row.VlanText);
        Assert.Equal("Cameras", row.VlanNameText);
    }

    [Theory]
    [InlineData(999)] // no such VLAN row
    [InlineData(0)]
    public void An_unresolvable_internal_id_is_unknown_never_shown_as_a_vlan(int id)
    {
        Assert.Equal("-", Row(new FdbEntry { DeviceId = 1, VlanId = id }).VlanText);
    }

    [Fact]
    public void Rows_pick_up_the_vlan_table_when_it_arrives()
    {
        var lookup = new FdbVlanLookup();
        var row = Row(new FdbEntry { DeviceId = 1, VlanId = 50 }, lookup);
        Assert.Equal("-", row.VlanText);

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        lookup.Resolver = new FdbVlanResolver(Vlans);
        row.RefreshVlan();

        // The row tells the grid, so it redraws rather than keeping "-".
        Assert.Contains(nameof(FdbItemViewModel.VlanText), changed);
        Assert.Contains(nameof(FdbItemViewModel.VlanNameText), changed);
        Assert.Equal("2074", row.VlanText);
        Assert.Equal("Cameras", row.VlanNameText);
    }

    [Fact]
    public void A_rule_is_flagged_only_for_columns_the_server_removed()
    {
        var rule = new AlertRule { Id = 1, Name = "Camera VLAN", Query = "SELECT * FROM devices,ports_fdb WHERE ports_fdb.vlan_id = 50" };
        RuleItemViewModel Item(Version? version) => new(rule, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }) { ServerVersion = version };

        Assert.False(Item(new Version(26, 10, 1)).NeedsReview);
        Assert.True(Item(new Version(26, 11, 0)).NeedsReview);
        Assert.Contains("ports_fdb.vlan_id", Item(new Version(26, 11, 0)).ReviewToolTip, StringComparison.Ordinal);
        Assert.True(Item(null).NeedsReview);
    }
}

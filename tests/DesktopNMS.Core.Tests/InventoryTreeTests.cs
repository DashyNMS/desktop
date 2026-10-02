using System.Text.Json;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class InventoryTreeTests
{
    private static InventoryEntry Entry(long index, long containedIn, long relPos = -1, string? name = null, string? cls = "module") => new()
    {
        Index = index,
        ContainedIn = containedIn,
        ParentRelPos = relPos,
        Name = name ?? $"E{index}",
        Class = cls,
    };

    [Fact]
    public void Children_hang_off_their_parents_index_not_their_database_id()
    {
        var tree = InventoryTree.Build(new[]
        {
            Entry(1001, 0, name: "Chassis", cls: "chassis"),
            Entry(3001, 1001, 2, "Fan Tray", "container"),
            Entry(11001, 3001, 1, "Fan 1", "fan"),
            Entry(2001, 1001, 1, "Backplane", "backplane"),
        });

        var chassis = Assert.Single(tree);
        Assert.Equal("Chassis", chassis.Entry.Name);
        Assert.Equal(0, chassis.Depth);

        // Ordered by position within the parent.
        Assert.Equal(new[] { "Backplane", "Fan Tray" }, chassis.Children.Select(c => c.Entry.Name));

        var fan = Assert.Single(chassis.Children[1].Children);
        Assert.Equal("Fan 1", fan.Entry.Name);
        Assert.Equal(2, fan.Depth);
    }

    [Fact]
    public void Siblings_without_a_position_go_last_in_index_order()
    {
        var tree = InventoryTree.Build(new[]
        {
            Entry(1, 0),
            Entry(30, 1, -1),
            Entry(20, 1, -1),
            Entry(10, 1, 5),
        });

        Assert.Equal(new long[] { 10, 20, 30 }, tree[0].Children.Select(c => c.Entry.Index));
    }

    [Fact]
    public void An_entry_whose_parent_is_missing_becomes_a_root_rather_than_vanishing()
    {
        var tree = InventoryTree.Build(new[] { Entry(1, 0), Entry(5, 999) });

        Assert.Equal(new long[] { 1, 5 }, tree.Select(n => n.Entry.Index));
    }

    [Fact]
    public void A_containment_loop_or_self_reference_never_hangs_and_loses_nothing()
    {
        var tree = InventoryTree.Build(new[] { Entry(1, 2), Entry(2, 1), Entry(3, 3) });

        Assert.Equal(3, tree.SelectMany(n => n.DescendantsAndSelf()).Count());
    }

    [Fact]
    public void A_duplicated_index_is_only_shown_once()
    {
        var tree = InventoryTree.Build(new[] { Entry(1, 0), Entry(1, 0) });

        Assert.Single(tree);
    }

    [Fact]
    public void Rows_parse_from_LibreNMS_including_its_string_flags_and_nulls()
    {
        const string json = """
            [{"entPhysical_id":182,"device_id":3,"entPhysicalIndex":1001,"entPhysicalDescr":"Aruba JL320A 2930M-24G-PoE+ Switch",
              "entPhysicalClass":"chassis","entPhysicalName":"Chassis","entPhysicalHardwareRev":"Rev 0","entPhysicalFirmwareRev":"WC.17.02.0007",
              "entPhysicalSoftwareRev":"WC.16.11.0024","entPhysicalAlias":"","entPhysicalAssetID":"","entPhysicalIsFRU":"true",
              "entPhysicalModelName":"JL320A","entPhysicalVendorType":"enterprises.11.2.3.7.11.181.4","entPhysicalSerialNum":"SG97JQL5N0",
              "entPhysicalContainedIn":0,"entPhysicalParentRelPos":-1,"entPhysicalMfgName":"Aruba","entPhysicalMfgDate":null,"ifIndex":null},
             {"entPhysical_id":200,"entPhysicalIndex":"20","entPhysicalContainedIn":"9001","entPhysicalClass":"port","entPhysicalName":"20",
              "entPhysicalIsFRU":"false","entPhysicalParentRelPos":"20","ifIndex":"20"}]
            """;

        var rows = JsonSerializer.Deserialize<List<InventoryEntry>>(json, LibreNmsJson.Options)!;

        Assert.Equal(1001, rows[0].Index);
        Assert.Equal("SG97JQL5N0", rows[0].Serial);
        Assert.True(rows[0].IsFieldReplaceable);
        Assert.Null(rows[0].IfIndex);
        Assert.Equal("Chassis", rows[0].DisplayName);

        Assert.Equal(9001, rows[1].ContainedIn);
        Assert.True(rows[1].IsPort);
        Assert.False(rows[1].IsFieldReplaceable);
        Assert.Equal(20, rows[1].IfIndex);
    }

    [Fact]
    public void An_entry_with_no_name_falls_back_to_its_description_then_its_index()
    {
        Assert.Equal("PSU", new InventoryEntry { Description = "PSU" }.DisplayName);
        Assert.Equal("Entity 7", new InventoryEntry { Index = 7 }.DisplayName);
    }
}

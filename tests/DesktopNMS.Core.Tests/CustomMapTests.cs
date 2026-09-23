using DesktopNMS.Core.CustomMaps;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class CustomMapStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dashynms-maps-" + Guid.NewGuid().ToString("N"));
    private readonly CustomMapStore _store;

    public CustomMapStoreTests()
    {
        _store = new CustomMapStore(_folder, NullLogger<CustomMapStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static CustomMapDocument SampleMap(string name = "Core network")
    {
        var map = new CustomMapDocument { Name = name, MenuGroup = "Sites" };
        var a = new CustomMapNode { Label = "sw1", DeviceId = 12, X = 100, Y = 100 };
        var b = new CustomMapNode { Label = "sw2", DeviceId = 13, X = 400, Y = 100 };
        map.Nodes.AddRange(new[] { a, b });
        map.Edges.Add(new CustomMapEdge { Node1Id = a.Id, Node2Id = b.Id, PortId = 991, MidX = 250, MidY = 100 });
        return map;
    }

    [Fact]
    public void Saved_maps_round_trip_as_one_map_file_each()
    {
        var map = SampleMap();
        _store.Save(map);

        Assert.True(File.Exists(Path.Combine(_folder, map.Id + ".map")));

        var loaded = _store.Load(map.Id)!;
        Assert.Equal("Core network", loaded.Name);
        Assert.Equal(2, loaded.Nodes.Count);
        Assert.Equal(991, loaded.Edges[0].PortId);
        Assert.Equal(12, loaded.Nodes[0].DeviceId);
    }

    [Fact]
    public void List_is_grouped_by_menu_group_then_name()
    {
        _store.Save(new CustomMapDocument { Name = "Zeta", MenuGroup = "B" });
        _store.Save(new CustomMapDocument { Name = "Alpha", MenuGroup = "B" });
        _store.Save(new CustomMapDocument { Name = "Beta", MenuGroup = "A" });

        Assert.Equal(new[] { "Beta", "Alpha", "Zeta" }, _store.List().Select(m => m.Name));
    }

    [Fact]
    public void Export_then_import_makes_a_separate_copy_without_overwriting()
    {
        var map = SampleMap();
        _store.Save(map);
        var exported = Path.Combine(_folder, "..", Guid.NewGuid().ToString("N") + ".map");

        try
        {
            _store.Export(map.Id, exported);
            var imported = _store.Import(exported);

            Assert.NotEqual(map.Id, imported.Id);
            Assert.Equal("Core network (imported)", imported.Name);
            Assert.Equal(2, _store.List().Count);
            Assert.Equal("Core network", _store.Load(map.Id)!.Name);
        }
        finally
        {
            File.Delete(exported);
        }
    }

    [Fact]
    public void Importing_something_that_isnt_a_map_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".map");
        File.WriteAllText(path, "this is not json");

        try
        {
            Assert.Throws<CustomMapFormatException>(() => _store.Import(path));
            Assert.Empty(_store.List());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_map_from_a_newer_app_version_is_refused()
    {
        var json = CustomMapSerializer.Serialize(new CustomMapDocument { FormatVersion = CustomMapDocument.CurrentFormatVersion + 1 });

        var ex = Assert.Throws<CustomMapFormatException>(() => CustomMapSerializer.Deserialize(json));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void An_unsafe_id_in_an_imported_file_is_replaced()
    {
        var map = SampleMap();
        map.Id = "..\\..\\evil";
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".map");
        File.WriteAllText(path, CustomMapSerializer.Serialize(map));

        try
        {
            var imported = _store.Import(path);

            Assert.True(imported.Id.All(char.IsAsciiLetterOrDigit));
            Assert.True(File.Exists(Path.Combine(_folder, imported.Id + ".map")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Links_to_missing_nodes_are_dropped_when_read()
    {
        var map = SampleMap();
        map.Edges.Add(new CustomMapEdge { Node1Id = map.Nodes[0].Id, Node2Id = "gone" });

        var read = CustomMapSerializer.Deserialize(CustomMapSerializer.Serialize(map));

        Assert.Single(read.Edges);
    }

    [Fact]
    public void Unused_embedded_images_are_pruned_on_save()
    {
        var map = SampleMap();
        map.Images["used"] = new CustomMapImage { Data = "AAAA" };
        map.Images["orphan"] = new CustomMapImage { Data = "BBBB" };
        map.Nodes[0].Style = CustomMapNodeStyles.Image;
        map.Nodes[0].ImageId = "used";

        _store.Save(map);

        Assert.Equal(new[] { "used" }, _store.Load(map.Id)!.Images.Keys);
    }

    [Fact]
    public void Deleting_removes_the_file()
    {
        var map = SampleMap();
        _store.Save(map);

        _store.Delete(map.Id);

        Assert.Null(_store.Load(map.Id));
        Assert.Empty(_store.List());
    }
}

public class LinkUtilisationTests
{
    [Theory]
    [InlineData(0, "#00ff00")]
    // 5.1 * 50 is 254.999... in floating point, which LibreNMS's own
    // parseInt truncates to 254 - so its 50% is #fffe00, not #ffff00.
    [InlineData(50, "#fffe00")]
    [InlineData(100, "#ff0000")]
    [InlineData(200, "#ff00ff")]
    [InlineData(-1, "#000000")]
    public void Gradient_matches_librenms(double percent, string expected)
    {
        Assert.Equal(expected, LinkUtilisation.GradientColour(percent), ignoreCase: true);
    }

    [Fact]
    public void Percent_is_minus_one_without_a_speed()
    {
        Assert.Equal(-1, LinkUtilisation.Percent(500, 0));
        Assert.Equal(-1, LinkUtilisation.Percent(500, null));
        Assert.Equal(50, LinkUtilisation.Percent(500_000_000, 1_000_000_000));
    }

    [Fact]
    public void Fixed_colours_use_the_highest_step_at_or_below()
    {
        var colours = new Dictionary<string, string> { ["0"] = "#00AA00", ["60"] = "#AAAA00", ["90"] = "#AA0000", ["-1"] = "#111111" };

        Assert.Equal("#00AA00", LinkUtilisation.Colour(59.9, colours));
        Assert.Equal("#AAAA00", LinkUtilisation.Colour(60, colours));
        Assert.Equal("#AA0000", LinkUtilisation.Colour(150, colours));
        Assert.Equal("#111111", LinkUtilisation.Colour(-1, colours));
    }

    [Theory]
    [InlineData(100_000L, 1.0)]
    [InlineData(1_000_000_000L, 2.5)]
    [InlineData(10_000_000_000L, 3.0)]
    public void Width_grows_with_speed_like_librenms(long speed, double width)
    {
        Assert.Equal(width, LinkUtilisation.Width(speed));
    }

    [Fact]
    public void Rates_are_formatted_with_units()
    {
        Assert.Equal("1.25 Gbps", LinkUtilisation.Rate(1_250_000_000));
        Assert.Equal("800 bps", LinkUtilisation.Rate(800));
    }

    [Fact]
    public void Default_legend_samples_the_gradient_to_150_percent()
    {
        var rows = LinkUtilisation.LegendRows(new CustomMapLegend { Steps = 7 });

        Assert.Equal("Unknown", rows[0].Label);
        Assert.Equal(8, rows.Count);
        Assert.Equal("150%", rows[^1].Label.Trim());
    }
}

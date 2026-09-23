using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class WebMercatorTests
{
    [Fact]
    public void Null_island_is_the_middle_of_the_world()
    {
        var p = WebMercator.ToWorld(0, 0);

        Assert.Equal(128, p.X, 6);
        Assert.Equal(128, p.Y, 6);
    }

    [Fact]
    public void Round_trips_a_real_location()
    {
        // Baku, one of the live server's locations.
        var world = WebMercator.ToWorld(40.372425, 49.851997);
        var (lat, lng) = WebMercator.ToLatLng(world);

        Assert.Equal(40.372425, lat, 6);
        Assert.Equal(49.851997, lng, 6);
    }

    [Fact]
    public void Matches_the_standard_slippy_map_tile_for_a_known_point()
    {
        // London (51.5074, -0.1278) is in OSM tile 16/32744/21792 - worked out
        // with the OSM wiki's asinh form of the formula, not this code's.
        var world = WebMercator.ToWorld(51.5074, -0.1278);
        var scale = Math.Pow(2, 16);

        Assert.Equal(32744, (int)(world.X * scale / WebMercator.TileSize));
        Assert.Equal(21792, (int)(world.Y * scale / WebMercator.TileSize));
    }

    [Fact]
    public void Latitudes_beyond_the_projection_limit_are_clamped()
    {
        Assert.Equal(0, WebMercator.ToWorld(90, 0).Y, 3);
    }
}

public class GeoLocationsTests
{
    private static Device Device(int id, int? locationId, double? lat = null, double? lng = null) =>
        new() { DeviceId = id, LocationId = locationId, Latitude = lat, Longitude = lng };

    [Fact]
    public void Devices_at_one_location_share_one_pin()
    {
        var locations = new[] { new Location { Id = 23, Name = "R-FIA-Pitlane", Latitude = 40.37, Longitude = 49.85 } };

        var placement = GeoLocations.Build(new[] { Device(1, 23), Device(2, 23) }, locations);

        var pin = Assert.Single(placement.Pins);
        Assert.Equal("R-FIA-Pitlane", pin.Name);
        Assert.Equal(new[] { 1, 2 }, pin.DeviceIds);
        Assert.Empty(placement.UnplacedDeviceIds);
    }

    [Fact]
    public void Devices_without_a_location_or_coordinates_are_unplaced()
    {
        var locations = new[] { new Location { Id = 5, Name = "No coords" } };

        var placement = GeoLocations.Build(new[] { Device(1, null), Device(2, 5) }, locations);

        Assert.Empty(placement.Pins);
        Assert.Equal(new[] { 1, 2 }, placement.UnplacedDeviceIds);
    }

    [Fact]
    public void The_devices_own_coordinates_are_the_fallback()
    {
        var placement = GeoLocations.Build(new[] { Device(1, 9, 51.5, -0.12) }, Array.Empty<Location>());

        var pin = Assert.Single(placement.Pins);
        Assert.Equal(51.5, pin.Latitude);
        Assert.Equal("Location 9", pin.Name);
    }

    [Fact]
    public void Zero_zero_counts_as_no_coordinates()
    {
        var locations = new[] { new Location { Id = 1, Name = "Unset", Latitude = 0, Longitude = 0 } };

        Assert.Empty(GeoLocations.Build(new[] { Device(1, 1) }, locations).Pins);
    }
}

public class TileUrlTemplateTests
{
    [Theory]
    [InlineData("{s}.tile.openstreetmap.org", "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png")]
    [InlineData("//tiles.example.com/osm", "https://tiles.example.com/osm/{z}/{x}/{y}.png")]
    [InlineData("https://tiles.example.com/{z}/{x}/{y}.png", "https://tiles.example.com/{z}/{x}/{y}.png")]
    [InlineData("http://10.0.0.5:8080/tile/{z}/{x}/{y}.png", "http://10.0.0.5:8080/tile/{z}/{x}/{y}.png")]
    public void Accepts_librenms_host_form_and_full_templates(string setting, string expected)
    {
        Assert.Equal(expected, TileUrlTemplate.Normalise(setting));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("https://tiles.example.com/{z}/map.png")]
    public void Unusable_settings_give_null(string? setting)
    {
        Assert.Null(TileUrlTemplate.Normalise(setting));
    }

    [Fact]
    public void Formats_a_tile_address()
    {
        Assert.Equal("https://tile.openstreetmap.org/5/17/10.png", TileUrlTemplate.Format(TileUrlTemplate.Default, 5, 17, 10));
        Assert.StartsWith("https://a.", TileUrlTemplate.Format("https://{s}.t.org/{z}/{x}/{y}.png", 1, 0, 0));
    }

    [Fact]
    public void Recognises_openstreetmap_for_the_credit()
    {
        Assert.True(TileUrlTemplate.IsOpenStreetMap(TileUrlTemplate.Default));
        Assert.False(TileUrlTemplate.IsOpenStreetMap("https://tiles.example.com/{z}/{x}/{y}.png"));
    }
}

public class PinClusteringTests
{
    [Fact]
    public void Close_pins_merge_and_distant_ones_do_not()
    {
        var points = new[] { new MapPoint(0, 0), new MapPoint(10, 0), new MapPoint(200, 200) };

        var clusters = PinClustering.Cluster(points, p => p, radius: 24);

        Assert.Equal(2, clusters.Count);
        Assert.Equal(2, clusters[0].Count);
        Assert.Single(clusters[1]);
    }
}

public class TileRecolourTests
{
    private static (byte R, byte G, byte B) Recolour(byte r, byte g, byte b)
    {
        var pixel = new byte[] { b, g, r, 255 };
        TileRecolour.ToDark(pixel, 0x11, 0x14, 0x1A);
        return (pixel[2], pixel[1], pixel[0]);
    }

    [Fact]
    public void Light_land_becomes_dark()
    {
        // OSM's land colour.
        var (r, g, b) = Recolour(0xF2, 0xEF, 0xE9);

        Assert.True(r < 40 && g < 40 && b < 40, $"got {r},{g},{b}");
    }

    [Fact]
    public void Dark_label_text_becomes_light()
    {
        var (r, g, b) = Recolour(0x33, 0x33, 0x33);

        Assert.True(r > 150 && g > 150 && b > 150, $"got {r},{g},{b}");
    }

    [Fact]
    public void Water_stays_blue()
    {
        // OSM's water colour - blue must still be the strongest channel.
        var (r, g, b) = Recolour(0xAA, 0xD3, 0xDF);

        Assert.True(b > r && b >= g, $"got {r},{g},{b}");
    }

    [Fact]
    public void Alpha_is_left_alone()
    {
        var pixel = new byte[] { 10, 20, 30, 77 };
        TileRecolour.ToDark(pixel, 0, 0, 0);

        Assert.Equal(77, pixel[3]);
    }
}

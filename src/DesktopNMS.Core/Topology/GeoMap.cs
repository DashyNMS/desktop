using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Topology;

/// <summary>
/// Web Mercator - the projection OpenStreetMap, Leaflet (what LibreNMS's
/// own world map uses) and every standard "slippy map" tile server share.
/// "World" coordinates here are pixels at zoom 0, where the whole world is
/// one <see cref="TileSize"/>-pixel square; at zoom z everything is 2^z
/// times bigger, and tile (x, y) covers world pixels x*256..(x+1)*256.
/// </summary>
public static class WebMercator
{
    public const int TileSize = 256;

    /// <summary>Beyond this latitude the projection runs to infinity - every tile server clips here.</summary>
    public const double MaxLatitude = 85.05112878;

    public static MapPoint ToWorld(double latitude, double longitude)
    {
        var lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        var x = (longitude + 180) / 360 * TileSize;
        var y = (1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * TileSize;
        return new MapPoint(x, y);
    }

    public static (double Latitude, double Longitude) ToLatLng(MapPoint world)
    {
        var longitude = world.X / TileSize * 360 - 180;
        var n = Math.PI - 2 * Math.PI * world.Y / TileSize;
        var latitude = 180 / Math.PI * Math.Atan(Math.Sinh(n));
        return (latitude, longitude);
    }
}

/// <summary>One pin on the Geographical map - a LibreNMS location and the devices in scope there.</summary>
public sealed class LocationPin
{
    public required int? LocationId { get; init; }

    public required string Name { get; init; }

    public required double Latitude { get; init; }

    public required double Longitude { get; init; }

    public required IReadOnlyList<int> DeviceIds { get; init; }

    public MapPoint World => WebMercator.ToWorld(Latitude, Longitude);
}

public sealed class GeoPlacement
{
    public required IReadOnlyList<LocationPin> Pins { get; init; }

    /// <summary>Devices in scope that can't be placed - no location, or a location with no coordinates.</summary>
    public required IReadOnlyList<int> UnplacedDeviceIds { get; init; }
}

/// <summary>
/// Groups devices into one pin per LibreNMS location (Geographical map,
/// issue #56 follow-up) - most locations hold several devices at identical
/// coordinates, so per-device pins would just stack.
/// </summary>
public static class GeoLocations
{
    /// <param name="devices">The devices in scope - the whole fleet or one group's members.</param>
    /// <param name="locations">LibreNMS's location list, for names and coordinates; a device's own joined lat/lng is the fallback.</param>
    public static GeoPlacement Build(IEnumerable<Device> devices, IEnumerable<Location> locations)
    {
        var byId = locations.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        var groups = new Dictionary<int, List<Device>>();
        var unplaced = new List<int>();

        foreach (var device in devices)
        {
            if (device.LocationId is not { } locationId)
            {
                unplaced.Add(device.DeviceId);
                continue;
            }

            if (!groups.TryGetValue(locationId, out var list))
            {
                list = new List<Device>();
                groups[locationId] = list;
            }

            list.Add(device);
        }

        var pins = new List<LocationPin>();

        foreach (var (locationId, members) in groups)
        {
            byId.TryGetValue(locationId, out var location);

            var latitude = location?.Latitude ?? members.Select(d => d.Latitude).FirstOrDefault(v => v is not null);
            var longitude = location?.Longitude ?? members.Select(d => d.Longitude).FirstOrDefault(v => v is not null);

            if (latitude is not { } lat || longitude is not { } lng || !IsPlausible(lat, lng))
            {
                unplaced.AddRange(members.Select(d => d.DeviceId));
                continue;
            }

            pins.Add(new LocationPin
            {
                LocationId = locationId,
                Name = location?.Name ?? members.Select(d => d.Location).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? $"Location {locationId}",
                Latitude = lat,
                Longitude = lng,
                DeviceIds = members.Select(d => d.DeviceId).OrderBy(id => id).ToList(),
            });
        }

        return new GeoPlacement
        {
            Pins = pins.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            UnplacedDeviceIds = unplaced.OrderBy(id => id).ToList(),
        };
    }

    /// <summary>
    /// Rejects coordinates that can't be real, including 0,0 - "Null
    /// Island", which is what an unset location often ends up as rather
    /// than null, and would otherwise put a pin in the Gulf of Guinea.
    /// </summary>
    private static bool IsPlausible(double latitude, double longitude) =>
        latitude is >= -90 and <= 90
        && longitude is >= -180 and <= 180
        && !(latitude == 0 && longitude == 0);
}

/// <summary>
/// The Geographical map's tile server address. Accepts either a full
/// template ("https://tile.example.com/{z}/{x}/{y}.png") or LibreNMS's own
/// host-only <c>leaflet.tile_url</c> form ("{s}.tile.openstreetmap.org"),
/// so a value copied straight from LibreNMS's settings works as-is.
/// </summary>
public static class TileUrlTemplate
{
    /// <summary>OpenStreetMap's standard tiles - LibreNMS's own default. The a/b/c subdomains are deprecated, so this uses the single host.</summary>
    public const string Default = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    private static readonly string[] Subdomains = { "a", "b", "c" };

    /// <summary>A full template with {z}/{x}/{y}, or null when the setting is unusable (then the default applies).</summary>
    public static string? Normalise(string? setting)
    {
        var value = setting?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!value.Contains("{z}", StringComparison.Ordinal))
        {
            // LibreNMS's form is just the host (optionally with a path);
            // Leaflet builds "//{host}/{z}/{x}/{y}.png" from it.
            value = value.TrimEnd('/') + "/{z}/{x}/{y}.png";
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }
        else if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        return value.Contains("{x}", StringComparison.Ordinal) && value.Contains("{y}", StringComparison.Ordinal)
            && Uri.TryCreate(Format(value, 0, 0, 0), UriKind.Absolute, out _)
            ? value
            : null;
    }

    public static string Format(string template, int zoom, int x, int y) =>
        template
            .Replace("{s}", Subdomains[(x + y) % Subdomains.Length], StringComparison.Ordinal)
            .Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>True for openstreetmap.org's own tiles - which carry a usage policy and need the "© OpenStreetMap contributors" credit.</summary>
    public static bool IsOpenStreetMap(string template) =>
        template.Contains("openstreetmap.org", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Merges map pins that land too close together on screen into one, so
/// neighbouring sites don't draw on top of each other when zoomed out -
/// the same idea as LibreNMS's own leaflet.group_radius. Greedy, in input
/// order: fine for the tens of locations a fleet has.
/// </summary>
public static class PinClustering
{
    public static List<List<T>> Cluster<T>(IReadOnlyList<T> items, Func<T, MapPoint> screen, double radius)
    {
        var clusters = new List<(List<T> Members, double X, double Y)>();

        foreach (var item in items)
        {
            var point = screen(item);
            var joined = false;

            for (var i = 0; i < clusters.Count; i++)
            {
                var (members, cx, cy) = clusters[i];
                var dx = cx - point.X;
                var dy = cy - point.Y;
                if (dx * dx + dy * dy <= radius * radius)
                {
                    members.Add(item);

                    // Keep the cluster centred on its members as it grows.
                    var n = members.Count;
                    clusters[i] = (members, cx + (point.X - cx) / n, cy + (point.Y - cy) / n);
                    joined = true;
                    break;
                }
            }

            if (!joined)
            {
                clusters.Add((new List<T> { item }, point.X, point.Y));
            }
        }

        return clusters.Select(c => c.Members).ToList();
    }
}

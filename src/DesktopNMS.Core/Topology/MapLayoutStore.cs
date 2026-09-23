using System.Text.Json;
using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Topology;

/// <summary>
/// Remembered network map node positions (issue #56), one set per map scope
/// (the whole fleet, or each device group), so a layout someone has tidied
/// by dragging survives a restart. Kept in its own file rather than
/// settings.json: positions are saved on every drag, and a settings save
/// raises Changed across the whole app.
/// </summary>
public interface IMapLayoutStore
{
    /// <summary>Saved positions for this scope, keyed by device id - empty if none.</summary>
    IReadOnlyDictionary<int, MapPoint> Get(string scopeKey);

    void Save(string scopeKey, IReadOnlyDictionary<int, MapPoint> positions);

    /// <summary>Forgets this scope's positions - the map's Reset layout.</summary>
    void Clear(string scopeKey);
}

public sealed class MapLayoutStore : IMapLayoutStore
{
    private readonly string _path;
    private readonly ILogger<MapLayoutStore> _logger;
    private readonly object _sync = new();
    private Dictionary<string, Dictionary<int, MapPoint>>? _layouts;

    public MapLayoutStore(ILogger<MapLayoutStore> logger)
        : this(Path.Combine(AppPaths.DataDirectory, "map-layouts.json"), logger)
    {
    }

    /// <summary>For tests - any file path.</summary>
    public MapLayoutStore(string path, ILogger<MapLayoutStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public IReadOnlyDictionary<int, MapPoint> Get(string scopeKey)
    {
        lock (_sync)
        {
            return Layouts().TryGetValue(scopeKey, out var positions)
                ? new Dictionary<int, MapPoint>(positions)
                : new Dictionary<int, MapPoint>();
        }
    }

    public void Save(string scopeKey, IReadOnlyDictionary<int, MapPoint> positions)
    {
        lock (_sync)
        {
            Layouts()[scopeKey] = new Dictionary<int, MapPoint>(positions);
            Write();
        }
    }

    public void Clear(string scopeKey)
    {
        lock (_sync)
        {
            if (Layouts().Remove(scopeKey))
            {
                Write();
            }
        }
    }

    private Dictionary<string, Dictionary<int, MapPoint>> Layouts()
    {
        if (_layouts is not null)
        {
            return _layouts;
        }

        _layouts = new Dictionary<string, Dictionary<int, MapPoint>>();

        try
        {
            if (File.Exists(_path))
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, StoredPoint>>>(File.ReadAllText(_path));
                if (stored is not null)
                {
                    foreach (var (scope, points) in stored)
                    {
                        _layouts[scope] = points.ToDictionary(p => p.Key, p => new MapPoint(p.Value.X, p.Value.Y));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A lost layout just means the map lays itself out again.
            _logger.LogWarning(ex, "Could not read {Path}; starting with no saved map layouts", _path);
        }

        return _layouts;
    }

    private void Write()
    {
        try
        {
            var stored = _layouts!.ToDictionary(
                s => s.Key,
                s => s.Value.ToDictionary(p => p.Key, p => new StoredPoint(Math.Round(p.Value.X, 1), Math.Round(p.Value.Y, 1))));

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write {Path}", _path);
        }
    }

    /// <summary>On-disk shape - a plain class with settable properties rather than the record struct, so System.Text.Json round-trips it without extra configuration.</summary>
    private sealed class StoredPoint
    {
        public StoredPoint()
        {
        }

        public StoredPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; set; }

        public double Y { get; set; }
    }
}

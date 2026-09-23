using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.CustomMaps;

/// <summary>The ".map" file format - indented JSON, so a map file is readable and diffable.</summary>
public static class CustomMapSerializer
{
    public const string FileExtension = ".map";

    /// <summary>Larger than any sensible map with a background photo; a guard against opening something that isn't one.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(CustomMapDocument document) => JsonSerializer.Serialize(document, Options);

    /// <exception cref="CustomMapFormatException">Not a map file, or one from a newer version of the app.</exception>
    public static CustomMapDocument Deserialize(string json)
    {
        CustomMapDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CustomMapDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new CustomMapFormatException("This isn't a DashyNMS map file - it couldn't be read.", ex);
        }

        if (document is null || string.IsNullOrWhiteSpace(document.Id))
        {
            throw new CustomMapFormatException("This isn't a DashyNMS map file.");
        }

        if (document.FormatVersion > CustomMapDocument.CurrentFormatVersion)
        {
            throw new CustomMapFormatException("This map was saved by a newer version of DashyNMS. Update the app to open it.");
        }

        Normalise(document);
        return document;
    }

    /// <summary>Fills anything a hand-edited or older file left out, and drops links to nodes that don't exist.</summary>
    private static void Normalise(CustomMapDocument document)
    {
        document.Name = string.IsNullOrWhiteSpace(document.Name) ? "Untitled map" : document.Name.Trim();
        document.Width = Math.Clamp(document.Width, 100, 20000);
        document.Height = Math.Clamp(document.Height, 100, 20000);
        document.NodeAlign = Math.Clamp(document.NodeAlign, 0, 500);
        document.Background ??= new CustomMapBackground();
        document.Legend ??= new CustomMapLegend();
        document.NodeDefaults ??= CustomMapNode.CreateDefault();
        document.EdgeDefaults ??= CustomMapEdge.CreateDefault();
        document.Nodes ??= new List<CustomMapNode>();
        document.Edges ??= new List<CustomMapEdge>();
        document.Images ??= new Dictionary<string, CustomMapImage>();

        var nodeIds = document.Nodes.Select(n => n.Id).ToHashSet();
        document.Edges.RemoveAll(e => !nodeIds.Contains(e.Node1Id) || !nodeIds.Contains(e.Node2Id) || e.Node1Id == e.Node2Id);
    }
}

public sealed class CustomMapFormatException : Exception
{
    public CustomMapFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>What the map list shows, without keeping every map (and its images) in memory.</summary>
public sealed record CustomMapSummary(string Id, string Name, string? MenuGroup, int NodeCount, DateTimeOffset Modified);

/// <summary>
/// Custom maps on disk: one self-contained "{id}.map" file per map in
/// %APPDATA%\DashyNMS\custom-maps\. Import/export are just reading and
/// writing that same format anywhere else.
/// </summary>
public interface ICustomMapStore
{
    /// <summary>Raised after any map is saved, imported or deleted - lists and the Default map setting refresh.</summary>
    event EventHandler? Changed;

    IReadOnlyList<CustomMapSummary> List();

    CustomMapDocument? Load(string id);

    void Save(CustomMapDocument document);

    void Delete(string id);

    /// <summary>
    /// Reads a map file from anywhere and adds it to the store. An id that's
    /// already in use gets a fresh one, and a clashing name has
    /// "(imported)" added, so importing never overwrites an existing map.
    /// </summary>
    /// <exception cref="CustomMapFormatException">Not a readable map file.</exception>
    CustomMapDocument Import(string path);

    void Export(string id, string path);
}

public sealed class CustomMapStore : ICustomMapStore
{
    private readonly string _folder;
    private readonly ILogger<CustomMapStore> _logger;
    private readonly object _sync = new();

    public CustomMapStore(ILogger<CustomMapStore> logger)
        : this(Path.Combine(AppPaths.DataDirectory, "custom-maps"), logger)
    {
    }

    /// <summary>For tests - any folder.</summary>
    public CustomMapStore(string folder, ILogger<CustomMapStore> logger)
    {
        _folder = folder;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CustomMapSummary> List()
    {
        lock (_sync)
        {
            if (!Directory.Exists(_folder))
            {
                return Array.Empty<CustomMapSummary>();
            }

            var summaries = new List<CustomMapSummary>();
            foreach (var file in Directory.EnumerateFiles(_folder, "*" + CustomMapSerializer.FileExtension))
            {
                if (TryRead(file) is { } map)
                {
                    summaries.Add(new CustomMapSummary(map.Id, map.Name, map.MenuGroup, map.Nodes.Count, map.Modified));
                }
            }

            return summaries
                .OrderBy(s => s.MenuGroup ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public CustomMapDocument? Load(string id)
    {
        lock (_sync)
        {
            return TryRead(PathFor(id));
        }
    }

    public void Save(CustomMapDocument document)
    {
        lock (_sync)
        {
            document.PruneImages();
            document.Modified = DateTimeOffset.Now;
            Directory.CreateDirectory(_folder);
            WriteAtomic(PathFor(document.Id), CustomMapSerializer.Serialize(document));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        lock (_sync)
        {
            var path = PathFor(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public CustomMapDocument Import(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new CustomMapFormatException("That file doesn't exist.");
        }

        if (info.Length > CustomMapSerializer.MaxFileBytes)
        {
            throw new CustomMapFormatException("That file is too large to be a DashyNMS map.");
        }

        var document = CustomMapSerializer.Deserialize(File.ReadAllText(path));

        lock (_sync)
        {
            var existing = List();
            if (existing.Any(m => m.Id == document.Id) || !IsSafeId(document.Id))
            {
                document.Id = CustomMapDocument.NewId();
            }

            if (existing.Any(m => string.Equals(m.Name, document.Name, StringComparison.OrdinalIgnoreCase)))
            {
                document.Name += " (imported)";
            }
        }

        Save(document);
        return document;
    }

    public void Export(string id, string path)
    {
        var document = Load(id) ?? throw new CustomMapFormatException("That map no longer exists.");
        WriteAtomic(path, CustomMapSerializer.Serialize(document));
    }

    /// <summary>Ids become file names, so only a plain hex/alphanumeric id is used as-is - anything else from an imported file is replaced.</summary>
    private static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit);

    private string PathFor(string id)
    {
        if (!IsSafeId(id))
        {
            throw new ArgumentException("Invalid map id.", nameof(id));
        }

        return Path.Combine(_folder, id + CustomMapSerializer.FileExtension);
    }

    private CustomMapDocument? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var document = CustomMapSerializer.Deserialize(File.ReadAllText(path));

            // The file name is the id the store finds it by - it wins over
            // whatever's inside (e.g. a map file copied in by hand under a
            // new name), so what's listed can always be opened again.
            document.Id = Path.GetFileNameWithoutExtension(path);
            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CustomMapFormatException)
        {
            _logger.LogWarning(ex, "Could not read custom map {Path}", path);
            return null;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}

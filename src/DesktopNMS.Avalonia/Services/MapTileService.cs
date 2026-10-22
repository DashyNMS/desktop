using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using DesktopNMS.Core.Topology;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Background map tiles for the Geographical and custom maps (#250, from the
/// WPF app's MapTileService). The same tiles LibreNMS's own Leaflet map uses
/// by default (OpenStreetMap), fetched within its tile usage policy: an
/// identifying User-Agent, no more than two downloads at once, and a local
/// disk cache (a week) so panning back, or reopening the app, doesn't
/// download a tile again.
/// </summary>
public interface IMapTileService : IMapTiles
{
    /// <summary>Raised on the UI thread when a requested tile arrives - the map redraws.</summary>
    event EventHandler? TileLoaded;

    /// <summary>The tile if it's to hand (memory or disk); otherwise null, and it's downloaded in the background.</summary>
    Bitmap? GetTile(string template, int zoom, int x, int y);

    /// <summary>The tile only if it's already in memory - never starts a download. For stand-in tiles while the real one loads.</summary>
    Bitmap? PeekTile(string template, int zoom, int x, int y);
}

public sealed class MapTileService : IMapTileService
{
    /// <summary>OpenStreetMap's policy: at most two parallel downloads per client.</summary>
    private const int MaxConcurrentDownloads = 2;

    /// <summary>Tiles to keep decoded in memory - a few screens' worth.</summary>
    private const int MemoryCacheSize = 600;

    /// <summary>Queued downloads beyond this are dropped rather than queued - fast panning would otherwise pile up requests for tiles long scrolled past. Anything still needed is asked for again on the next redraw.</summary>
    private const int MaxPending = 48;

    private static readonly TimeSpan DiskCacheLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly ILogger<MapTileService> _logger;
    private readonly SemaphoreSlim _downloads = new(MaxConcurrentDownloads);
    private readonly string _cacheRoot;

    private readonly Dictionary<string, Bitmap> _memory = new();
    private readonly LinkedList<string> _memoryOrder = new();
    private readonly HashSet<string> _pending = new();
    private readonly Dictionary<string, DateTime> _failedUntil = new();

    public MapTileService(ILogger<MapTileService> logger)
    {
        _logger = logger;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"DashyNMS/{version} (+https://github.com/DashyNMS/desktop)");

        // Local, not roaming, app data: a tile cache has no business
        // following a user between machines. Shared with the WPF app's - the
        // same tiles from the same servers.
        _cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashyNMS", "tiles");
    }

    public event EventHandler? TileLoaded;

    public Bitmap? GetTile(string template, int zoom, int x, int y)
    {
        var url = TileUrlTemplate.Format(template, zoom, x, y);

        // In the dark theme tiles are recoloured as they're decoded; the
        // theme can change while the app runs, so each look is kept apart.
        // The disk cache always keeps the original tile.
        var dark = DarkBackground();
        var key = Key(url, dark);

        if (_memory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_pending.Contains(key)
            || (_failedUntil.TryGetValue(url, out var until) && DateTime.UtcNow < until)
            || _pending.Count >= MaxPending)
        {
            return null;
        }

        _pending.Add(key);
        _ = LoadAsync(url, key, DiskPath(template, zoom, x, y), dark);
        return null;
    }

    public Bitmap? PeekTile(string template, int zoom, int x, int y) =>
        _memory.TryGetValue(Key(TileUrlTemplate.Format(template, zoom, x, y), DarkBackground()), out var cached) ? cached : null;

    private static string Key(string url, Color? dark) => dark is null ? url : url + "#dark";

    /// <summary>The dark theme's background colour, which tiles are blended toward; null in the light theme.</summary>
    private static Color? DarkBackground()
    {
        if (Application.Current is not { } app || app.ActualThemeVariant != ThemeVariant.Dark)
        {
            return null;
        }

        return app.TryFindResource("BackgroundColor", ThemeVariant.Dark, out var value) && value is Color colour
            ? colour
            : Color.FromRgb(0x11, 0x14, 0x1A);
    }

    private async Task LoadAsync(string url, string key, string diskPath, Color? darkBackground)
    {
        Bitmap? image = null;

        try
        {
            var bytes = await Task.Run(() => ReadDisk(diskPath)).ConfigureAwait(false);

            if (bytes is null)
            {
                await _downloads.WaitAsync().ConfigureAwait(false);
                try
                {
                    bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                }
                finally
                {
                    _downloads.Release();
                }

                await Task.Run(() => WriteDisk(diskPath, bytes)).ConfigureAwait(false);
            }

            var decoded = bytes;
            image = await Task.Run(() => Decode(decoded, darkBackground)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "Could not load map tile {Url}", url);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _pending.Remove(key);

            if (image is null)
            {
                _failedUntil[url] = DateTime.UtcNow + FailureBackoff;
                return;
            }

            Remember(key, image);
            TileLoaded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Remember(string key, Bitmap image)
    {
        _memory[key] = image;
        _memoryOrder.AddLast(key);

        while (_memoryOrder.Count > MemoryCacheSize)
        {
            _memory.Remove(_memoryOrder.First!.Value);
            _memoryOrder.RemoveFirst();
        }
    }

    private static Bitmap Decode(byte[] bytes, Color? darkBackground)
    {
        using var stream = new MemoryStream(bytes);
        if (darkBackground is not { } background)
        {
            return new Bitmap(stream);
        }

        // Recoloured pixel by pixel, which works on BGRA; a bitmap decoded
        // as RGBA has its red and blue swapped around the recolour.
        var bitmap = WriteableBitmap.Decode(stream);
        using (var frame = bitmap.Lock())
        {
            var length = frame.RowBytes * frame.Size.Height;
            var pixels = new byte[length];
            Marshal.Copy(frame.Address, pixels, 0, length);

            var rgba = frame.Format == PixelFormat.Rgba8888;
            if (rgba)
            {
                SwapRedBlue(pixels);
            }

            TileRecolour.ToDark(pixels, background.R, background.G, background.B);

            if (rgba)
            {
                SwapRedBlue(pixels);
            }

            Marshal.Copy(pixels, 0, frame.Address, length);
        }

        return bitmap;
    }

    private static void SwapRedBlue(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }
    }

    private static byte[]? ReadDisk(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < DiskCacheLifetime
            ? File.ReadAllBytes(path)
            : null;
    }

    private void WriteDisk(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation - the tile is still shown.
            _logger.LogDebug(ex, "Could not cache map tile to {Path}", path);
        }
    }

    /// <summary>One folder per tile server (hashed template), so switching servers never mixes their tiles.</summary>
    private string DiskPath(string template, int zoom, int x, int y)
    {
        var server = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template)))[..16];
        return Path.Combine(_cacheRoot, server, zoom.ToString(), x.ToString(), y + ".tile");
    }
}

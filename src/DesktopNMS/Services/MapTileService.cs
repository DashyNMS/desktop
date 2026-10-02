using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Background map tiles for the Geographical map. Same tiles LibreNMS's own
/// Leaflet map uses by default (OpenStreetMap), fetched within its tile
/// usage policy: an identifying User-Agent, no more than two downloads at
/// once, and a local disk cache (a week) so panning back over the same area,
/// or reopening the app, doesn't download it again.
/// </summary>
public interface IMapTileService
{
    /// <summary>Raised on the UI thread when a requested tile arrives - the map redraws.</summary>
    event EventHandler? TileLoaded;

    /// <summary>The tile if it's to hand (memory or disk); otherwise null, and it's downloaded in the background.</summary>
    BitmapSource? GetTile(string template, int zoom, int x, int y);

    /// <summary>The tile only if it's already in memory - never starts a download. For stand-in tiles while the real one loads.</summary>
    BitmapSource? PeekTile(string template, int zoom, int x, int y);
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
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _downloads = new(MaxConcurrentDownloads);
    private readonly string _cacheRoot;

    private readonly Dictionary<string, BitmapSource> _memory = new();
    private readonly LinkedList<string> _memoryOrder = new();
    private readonly HashSet<string> _pending = new();
    private readonly Dictionary<string, DateTime> _failedUntil = new();

    /// <summary>
    /// Dark theme: tiles are recoloured to suit it as they're decoded (see
    /// <see cref="TileRecolour"/>). Read once - a theme change needs a
    /// restart anyway (see App.ApplyTheme). The disk cache always keeps the
    /// original tile, so switching theme never re-downloads anything.
    /// </summary>
    private readonly bool _dark;

    /// <summary>The theme's background colour, which dark tiles are blended toward - looked up on first use, on the UI thread.</summary>
    private Color? _background;

    public MapTileService(ISettingsStore settings, ILogger<MapTileService> logger)
    {
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dark = ThemeState.IsDark;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"DashyNMS/{version} (+https://github.com/DashyNMS/desktop)");

        // Local, not roaming, app data: a tile cache has no business
        // following a user between machines.
        _cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashyNMS", "tiles");
    }

    public event EventHandler? TileLoaded;

    public BitmapSource? GetTile(string template, int zoom, int x, int y)
    {
        var url = TileUrlTemplate.Format(template, zoom, x, y);

        if (_memory.TryGetValue(url, out var cached))
        {
            return cached;
        }

        if (_pending.Contains(url)
            || (_failedUntil.TryGetValue(url, out var until) && DateTime.UtcNow < until)
            || _pending.Count >= MaxPending)
        {
            return null;
        }

        _background ??= Application.Current?.TryFindResource("BackgroundColor") as Color? ?? Color.FromRgb(0x11, 0x14, 0x1A);

        _pending.Add(url);
        _ = LoadAsync(url, DiskPath(template, zoom, x, y), _dark ? _background : null);
        return null;
    }

    public BitmapSource? PeekTile(string template, int zoom, int x, int y) =>
        _memory.TryGetValue(TileUrlTemplate.Format(template, zoom, x, y), out var cached) ? cached : null;

    /// <param name="darkBackground">Set in the dark theme: recolour the tile, blending toward this.</param>
    private async Task LoadAsync(string url, string diskPath, Color? darkBackground)
    {
        BitmapSource? image = null;

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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not load map tile {Url}", url);
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _pending.Remove(url);

            if (image is null)
            {
                _failedUntil[url] = DateTime.UtcNow + FailureBackoff;
                return;
            }

            Remember(url, image);
            TileLoaded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Remember(string url, BitmapSource image)
    {
        _memory[url] = image;
        _memoryOrder.AddLast(url);

        while (_memoryOrder.Count > MemoryCacheSize)
        {
            _memory.Remove(_memoryOrder.First!.Value);
            _memoryOrder.RemoveFirst();
        }
    }

    private static BitmapSource Decode(byte[] bytes, Color? darkBackground)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        if (darkBackground is not { } background)
        {
            return image;
        }

        // Tiles arrive as paletted PNGs or JPEGs; normalise to BGRA so the
        // recolour can work pixel by pixel.
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        TileRecolour.ToDark(pixels, background.R, background.G, background.B);

        var dark = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        dark.Freeze();
        return dark;
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

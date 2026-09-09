using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Fetches the connected LibreNMS server's own branding so the shell header
/// can show it next to the tabs - a small per-server touch, distinct from
/// the app's own icon. Best-effort only: plenty of installs have neither a
/// custom logo nor a favicon, or the server is unreachable for it, and that
/// is not an error.
/// </summary>
public interface IServerBrandingService
{
    /// <summary>The connected server's logo/favicon, or null if there isn't one / it hasn't loaded yet.</summary>
    BitmapImage? Logo { get; }

    event EventHandler? Changed;
}

public sealed class ServerBrandingService : IServerBrandingService, IDisposable
{
    /// <summary>
    /// Tried in order, first hit wins. "images/custom/logo.png" is LibreNMS's
    /// own convention for a site's custom-branded logo
    /// (<c>$config['title_image']['custom']</c>); favicon.ico is the generic
    /// fallback for installs that never set one.
    /// </summary>
    private static readonly string[] CandidatePaths = { "images/custom/logo.png", "favicon.ico" };

    private readonly ISessionService _session;
    private readonly ILogger<ServerBrandingService> _logger;

    private CancellationTokenSource? _cts;
    private BitmapImage? _logo;

    public ServerBrandingService(ISessionService session, ILogger<ServerBrandingService> logger)
    {
        _session = session;
        _logger = logger;

        _session.StateChanged += OnSessionStateChanged;

        if (_session.Connection is { } connection)
        {
            _ = RefreshAsync(connection);
        }
    }

    public BitmapImage? Logo
    {
        get => _logo;
        private set
        {
            _logo = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        _cts?.Cancel();

        if (_session.Connection is { } connection)
        {
            _ = RefreshAsync(connection);
        }
        else
        {
            Logo = null;
        }
    }

    private async Task RefreshAsync(LibreNmsConnection connection)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        using var handler = new HttpClientHandler();
        if (connection.AllowUntrustedCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

        foreach (var path in CandidatePaths)
        {
            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var url = new Uri(connection.WebRoot, path);
                var bytes = await http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(true);

                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }

                bitmap.Freeze();
                Logo = bitmap;
                return;
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer connection, or shutting down.
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or WebException or NotSupportedException or IOException)
            {
                // Not found, an unreachable server, or a response that is not
                // a decodable image - try the next candidate.
                _logger.LogDebug(ex, "Could not fetch {Path} from the server", path);
            }
        }

        Logo = null;
    }

    public void Dispose()
    {
        _session.StateChanged -= OnSessionStateChanged;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

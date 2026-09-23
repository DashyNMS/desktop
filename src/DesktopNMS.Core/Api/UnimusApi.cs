using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Api;

/// <summary>
/// HTTP implementation of <see cref="IUnimusApi"/>. A single long-lived
/// instance registered in DI, reconfigured via <see cref="Configure"/>/
/// <see cref="Clear"/> as the user sets up or disables the integration in
/// Settings - the same lifetime shape as <see cref="LibreNmsTransport"/>,
/// so nothing else has to know when Unimus becomes available or not.
/// </summary>
/// <remarks>
/// Unimus returns plain, well-typed JSON (unlike LibreNMS's MySQL-string
/// quirks), so this uses ordinary <see cref="JsonSerializerOptions"/> rather
/// than <c>LibreNmsJson.Options</c>.
///
/// Deliberately simpler than <see cref="LibreNmsTransport"/> otherwise: no
/// stale-pooled-connection or transient-failure retry loop. That machinery
/// exists there because LibreNMS is polled continuously in the background,
/// so a once-in-a-while dropped connection is expected and worth absorbing
/// silently. Every Unimus call is user-initiated (opening the Config tab,
/// clicking Diff or Backup now) - a single clear failure the user can retry
/// by clicking again is the right shape here, not silent background retries.
/// </remarks>
public sealed class UnimusApi : IUnimusApi, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<UnimusApi> _logger;
    private readonly object _sync = new();

    private HttpClient? _http;
    private HttpMessageHandler? _handler;
    private UnimusConnection? _connection;

    public UnimusApi(ILogger<UnimusApi> logger)
    {
        _logger = logger;
    }

    public bool IsConfigured
    {
        get
        {
            lock (_sync)
            {
                return _http is not null;
            }
        }
    }

    /// <summary>Points this client at a Unimus instance. Safe to call repeatedly; the previous client and handler are disposed.</summary>
    public void Configure(UnimusConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            UseCookies = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (connection.AllowUntrustedCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = connection.ApiBase,
            Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
        };

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DashyNMS/1.1 (+https://librenms.org)");

        HttpClient? oldHttp;
        HttpMessageHandler? oldHandler;

        lock (_sync)
        {
            oldHttp = _http;
            oldHandler = _handler;
            _http = http;
            _handler = handler;
            _connection = connection;
        }

        oldHttp?.Dispose();
        oldHandler?.Dispose();

        _logger.LogInformation("Unimus client configured for {ApiBase}", connection.ApiBase);
    }

    /// <summary>Disables the integration; subsequent calls fail as "not configured".</summary>
    public void Clear()
    {
        HttpClient? oldHttp;
        HttpMessageHandler? oldHandler;

        lock (_sync)
        {
            oldHttp = _http;
            oldHandler = _handler;
            _http = null;
            _handler = null;
            _connection = null;
        }

        oldHttp?.Dispose();
        oldHandler?.Dispose();
    }

    public async Task<UnimusDevice?> FindDeviceAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        using var response = await SendAsync(HttpMethod.Get, $"devices/findByAddress/{Uri.EscapeDataString(address)}", cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await response.Content.ReadFromJsonAsync<UnimusEnvelope<UnimusDevice>>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return envelope?.Data;
    }

    public async Task<IReadOnlyList<UnimusDevice>> ListAllDevicesAsync(CancellationToken cancellationToken = default)
    {
        const int pageSize = 500;
        var devices = new List<UnimusDevice>();
        var page = 0;
        int totalPages;

        do
        {
            using var response = await SendAsync(HttpMethod.Get, $"devices?page={page}&size={pageSize}", cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            var envelope = await response.Content.ReadFromJsonAsync<UnimusPagedEnvelope<UnimusDevice>>(JsonOptions, cancellationToken).ConfigureAwait(false);
            devices.AddRange(envelope?.Data ?? new List<UnimusDevice>());
            totalPages = envelope?.Paginator?.TotalPages ?? 1;
            page++;
        }
        while (page < totalPages);

        return devices;
    }

    public async Task<UnimusBackup?> GetLatestBackupAsync(int unimusDeviceId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"devices/{unimusDeviceId}/backups/latest", cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await response.Content.ReadFromJsonAsync<UnimusEnvelope<UnimusBackup>>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return envelope?.Data;
    }

    public async Task<UnimusBackupPage> GetBackupsAsync(int unimusDeviceId, int page = 0, int size = 50, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"devices/{unimusDeviceId}/backups?page={page}&size={size}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await response.Content.ReadFromJsonAsync<UnimusPagedEnvelope<UnimusBackup>>(JsonOptions, cancellationToken).ConfigureAwait(false);

        return new UnimusBackupPage
        {
            Backups = envelope?.Data ?? new List<UnimusBackup>(),
            TotalCount = envelope?.Paginator?.TotalCount ?? 0,
            TotalPages = envelope?.Paginator?.TotalPages ?? 1,
            Page = envelope?.Paginator?.Page ?? page,
        };
    }

    public async Task<string?> GetBackupContentAsync(int unimusDeviceId, int backupId, int page = 0, int size = 50, CancellationToken cancellationToken = default)
    {
        var result = await GetBackupsAsync(unimusDeviceId, page, size, cancellationToken).ConfigureAwait(false);
        return result.Backups.FirstOrDefault(b => b.Id == backupId)?.Content;
    }

    public async Task<UnimusBackupDiff?> GetDiffAsync(int origBackupId, int revBackupId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"backups/diff?origId={origBackupId}&revId={revBackupId}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await response.Content.ReadFromJsonAsync<UnimusEnvelope<UnimusBackupDiff>>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return envelope?.Data;
    }

    public async Task<UnimusBackupJobResult> TriggerBackupAsync(int unimusDeviceId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, $"jobs/backup?id={unimusDeviceId}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var envelope = await response.Content.ReadFromJsonAsync<UnimusEnvelope<UnimusBackupJobResult>>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return envelope?.Data ?? new UnimusBackupJobResult();
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "devices/findByAddress/dashynms-connection-test", cancellationToken).ConfigureAwait(false);

        // 404 (no such device) is exactly what a working, authenticated
        // connection returns for a made-up address - success for this purpose.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUrl, CancellationToken cancellationToken)
    {
        HttpClient http;
        lock (_sync)
        {
            http = _http ?? throw new UnimusApiException("Unimus is not configured.");
        }

        using var request = new HttpRequestMessage(method, relativeUrl);

        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UnimusApiException($"The request to Unimus timed out after {http.Timeout.TotalSeconds:0} seconds.", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not reach Unimus at {BaseAddress}", http.BaseAddress);
            throw new UnimusApiException(DescribeTransportFailure(ex), innerException: ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnimusApiException("Unimus rejected the API token.", response.StatusCode);
        }

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort only - fall through with no extra detail.
        }

        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : $": {body.Trim()}";
        throw new UnimusApiException($"Unimus returned {(int)response.StatusCode} {response.ReasonPhrase}{detail}", response.StatusCode);
    }

    private static string DescribeTransportFailure(HttpRequestException ex) => ex.InnerException switch
    {
        System.Security.Authentication.AuthenticationException => "Could not verify Unimus's TLS certificate. If it uses a self-signed or internal certificate, enable \"Ignore SSL certificate errors\" in Settings.",
        _ => $"Could not reach Unimus: {ex.Message}",
    };

    public void Dispose()
    {
        _http?.Dispose();
        _handler?.Dispose();
    }
}

/// <summary>Unimus's standard single-object envelope: <c>{"data": {...}}</c>.</summary>
internal sealed class UnimusEnvelope<T>
{
    public T? Data { get; set; }
}

/// <summary>Unimus's paginated-list envelope: <c>{"data": [...], "paginator": {...}}</c>.</summary>
internal sealed class UnimusPagedEnvelope<T>
{
    public List<T>? Data { get; set; }

    public UnimusPaginator? Paginator { get; set; }
}

internal sealed class UnimusPaginator
{
    public int TotalCount { get; set; }

    public int TotalPages { get; set; }

    public int Page { get; set; }
}

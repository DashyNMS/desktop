using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Client for a Graylog server's REST API (issue #114) - a third
/// integration this app talks to directly, not through LibreNMS: LibreNMS's
/// own Graylog client (<c>app/ApiClients/GraylogApi.php</c>) only drives its
/// web UI's Graylog pages and isn't reachable via its versioned API. This
/// covers the same two calls that client makes - list streams, and a
/// relative-time search - against the same endpoints.
/// </summary>
public interface IGraylogApi
{
    /// <summary>True once <see cref="Configure"/> has been called and not since undone by <see cref="Clear"/> - the integration is enabled and set up, not necessarily reachable.</summary>
    bool IsConfigured { get; }

    void Configure(GraylogConnection connection);

    void Clear();

    /// <summary>GET streams - every stream the account can read.</summary>
    Task<IReadOnlyList<GraylogStream>> GetStreamsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// GET search/universal/relative - the same call, with the same
    /// parameters, as LibreNMS's <c>GraylogApi::query</c>.
    /// </summary>
    /// <param name="query">Graylog search syntax - see <see cref="Graylog.GraylogQuery"/>.</param>
    /// <param name="rangeSeconds">How far back to search; 0 searches all time.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="offset">Messages to skip.</param>
    /// <param name="sort">"field:asc" or "field:desc"; null for Graylog's default.</param>
    /// <param name="filter">e.g. "streams:{id}"; null for every stream.</param>
    Task<GraylogSearchResult> SearchAsync(
        string query,
        int rangeSeconds,
        int limit,
        int offset,
        string? sort = null,
        string? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Verifies the configured address and credentials by listing streams, the lightest call LibreNMS itself makes. Returns how many streams the account can see.</summary>
    Task<int> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP implementation of <see cref="IGraylogApi"/> - a single long-lived
/// instance reconfigured from Settings, the same lifetime shape (and the
/// same no-silent-retry reasoning, see its remarks) as <see cref="UnimusApi"/>.
/// </summary>
public sealed class GraylogApi : IGraylogApi, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<GraylogApi> _logger;
    private readonly object _sync = new();

    private HttpClient? _http;
    private HttpMessageHandler? _handler;
    private GraylogConnection? _connection;

    public GraylogApi(ILogger<GraylogApi> logger)
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

    public void Configure(GraylogConnection connection)
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
            BaseAddress = connection.Root,
            Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
        };

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.Username + ":" + connection.Password));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
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

        _logger.LogInformation("Graylog client configured for {Root}", connection.Root);
    }

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

    public async Task<IReadOnlyList<GraylogStream>> GetStreamsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(c => c.StreamsPath, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var list = await ReadJsonAsync<GraylogStreamList>(response, cancellationToken).ConfigureAwait(false);
        return list?.Streams ?? new List<GraylogStream>();
    }

    public async Task<GraylogSearchResult> SearchAsync(
        string query,
        int rangeSeconds,
        int limit,
        int offset,
        string? sort = null,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            "query=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(query) ? "*" : query),
            "range=" + Math.Max(0, rangeSeconds).ToString(CultureInfo.InvariantCulture),
            "limit=" + Math.Max(0, limit).ToString(CultureInfo.InvariantCulture),
            "offset=" + Math.Max(0, offset).ToString(CultureInfo.InvariantCulture),
        };

        // Left out entirely when null, as LibreNMS's HTTP client does.
        if (!string.IsNullOrEmpty(sort))
        {
            parameters.Add("sort=" + Uri.EscapeDataString(sort));
        }

        if (!string.IsNullOrEmpty(filter))
        {
            parameters.Add("filter=" + Uri.EscapeDataString(filter));
        }

        var queryString = string.Join("&", parameters);

        using var response = await SendAsync(c => c.SearchPath + "?" + queryString, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadJsonAsync<GraylogSearchResult>(response, cancellationToken).ConfigureAwait(false)
               ?? new GraylogSearchResult();
    }

    public async Task<int> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var streams = await GetStreamsAsync(cancellationToken).ConfigureAwait(false);
        return streams.Count;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<GraylogConnection, string> relativeUrl, CancellationToken cancellationToken)
    {
        HttpClient http;
        GraylogConnection connection;
        lock (_sync)
        {
            http = _http ?? throw new GraylogApiException("Graylog is not configured.");
            connection = _connection!;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl(connection));

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
            throw new GraylogApiException($"The request to Graylog timed out after {http.Timeout.TotalSeconds:0} seconds.", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not reach Graylog at {BaseAddress}", http.BaseAddress);
            throw new GraylogApiException(DescribeTransportFailure(ex), innerException: ex);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // Most often the address points at Graylog's web interface (or a
            // proxy's login page) rather than its API, which answers with HTML.
            throw new GraylogApiException("Graylog's reply wasn't the JSON its API returns - check the server address, port and version.", response.StatusCode, ex);
        }
        catch (NotSupportedException ex)
        {
            throw new GraylogApiException("Graylog's reply wasn't the JSON its API returns - check the server address, port and version.", response.StatusCode, ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new GraylogApiException("Graylog rejected the username or password.", response.StatusCode);
        }

        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new GraylogApiException("This Graylog account isn't allowed to do that - it needs permission to read streams and search messages.", response.StatusCode);
        }

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new GraylogApiException("Graylog's API wasn't found at this address - check the server address, port and version.", response.StatusCode);
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

        throw new GraylogApiException($"Graylog returned {(int)response.StatusCode} {response.ReasonPhrase}{DescribeErrorBody(body)}", response.StatusCode);
    }

    /// <summary>Graylog's errors are <c>{"type":"ApiError","message":"..."}</c> - just the message is worth showing (a bad search query's reason lands here).</summary>
    private static string DescribeErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return ": " + message.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON - fall through to the trimmed raw body.
        }

        var trimmed = body.Trim();
        return ": " + (trimmed.Length > 300 ? trimmed[..300] + "..." : trimmed);
    }

    private static string DescribeTransportFailure(HttpRequestException ex) => ex.InnerException switch
    {
        System.Security.Authentication.AuthenticationException => "Could not verify Graylog's TLS certificate. If it uses a self-signed or internal certificate, enable \"Ignore SSL certificate errors\" in Settings.",
        _ => $"Could not reach Graylog: {ex.Message}",
    };

    public void Dispose()
    {
        _http?.Dispose();
        _handler?.Dispose();
    }
}

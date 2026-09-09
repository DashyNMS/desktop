using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DesktopNMS.Core.Json;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Api;

/// <summary>
/// HTTP transport for the LibreNMS API. Holds a single <see cref="HttpClient"/>
/// that is rebuilt whenever the connection details change.
/// </summary>
public sealed class LibreNmsTransport : ILibreNmsTransport, IDisposable
{
    private readonly ILogger<LibreNmsTransport> _logger;
    private readonly object _sync = new();

    private HttpClient? _http;
    private HttpMessageHandler? _handler;
    private LibreNmsConnection? _connection;
    private bool _disposed;

    public LibreNmsTransport(ILogger<LibreNmsTransport> logger)
    {
        _logger = logger;
    }

    public LibreNmsConnection? Connection
    {
        get
        {
            lock (_sync)
            {
                return _connection;
            }
        }
    }

    /// <summary>
    /// Points the transport at a LibreNMS instance. Safe to call repeatedly;
    /// the previous client and handler are disposed.
    /// </summary>
    public void Configure(LibreNmsConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // A plain HttpClientHandler leaves .NET's default pooled-connection
        // lifetime (effectively unbounded) in place. Many LibreNMS installs sit
        // behind a reverse proxy (openresty/nginx) that silently drops
        // keep-alive connections after a short idle period; reusing one of
        // those from the pool surfaces as an ObjectDisposedException /
        // "read operation failed" deep inside SslStream on the next poll, and
        // every subsequent request through that connection fails until the app
        // is restarted. SocketsHttpHandler is used instead so idle connections
        // are proactively evicted well before a typical proxy timeout.
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
            // Opt-in only: many LibreNMS installs sit behind an internal CA.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = connection.ApiBase,
            Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
        };

        http.DefaultRequestHeaders.Add("X-Auth-Token", connection.ApiToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DashyNMS/0.1 (+https://librenms.org)");

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

        _logger.LogInformation("LibreNMS transport configured for {ApiBase}", connection.ApiBase);
    }

    /// <summary>Drops the current connection; subsequent calls fail as "not signed in".</summary>
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

    public async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        // A request that lands on a pooled connection the server (or an
        // intervening proxy) closed moments earlier hangs until HttpClient's
        // own timeout fires, surfacing as a generic OperationCanceledException
        // with an IOException/ObjectDisposedException underneath rather than a
        // clean connection-refused error - there is no way to distinguish that
        // from a genuinely slow server up front. One retry on a fresh
        // connection resolves it without the caller ever seeing it; a second
        // failure is treated as real.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(method, relativeUrl, body, cancellationToken).ConfigureAwait(false);
            }
            catch (LibreNmsApiException ex) when (attempt == 1 && ex.LooksLikeStaleConnection && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Retrying {Url} after a possible stale pooled connection", relativeUrl);
            }
        }
    }

    private async Task<JsonDocument> SendOnceAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken cancellationToken)
    {
        HttpClient http;
        lock (_sync)
        {
            http = _http ?? throw new LibreNmsApiException("Not connected to a LibreNMS server.");
        }

        using var request = new HttpRequestMessage(method, relativeUrl);

        if (body is not null)
        {
            var payload = JsonSerializer.Serialize(body, LibreNmsJson.Options);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new LibreNmsApiException(
                $"The request to LibreNMS timed out after {http.Timeout.TotalSeconds:0} seconds.",
                innerException: ex,
                looksLikeStaleConnection: IsStaleConnectionFailure(ex));
        }
        catch (HttpRequestException ex)
        {
            throw new LibreNmsApiException(DescribeTransportFailure(ex), innerException: ex, looksLikeStaleConnection: IsStaleConnectionFailure(ex));
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseEnvelope(response.StatusCode, content, relativeUrl);
        }
    }

    /// <summary>
    /// True when the failure's cause, however deeply wrapped, is the socket or
    /// TLS layer being torn down mid-read/write - the signature of reusing a
    /// connection the far end already closed, as opposed to a slow response or
    /// a real network-down condition.
    /// </summary>
    private static bool IsStaleConnectionFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is ObjectDisposedException or IOException or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<T>> GetCollectionAsync<T>(
        string relativeUrl,
        string collectionProperty,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync(HttpMethod.Get, relativeUrl, body: null, cancellationToken)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty(collectionProperty, out var collection)
            || collection.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<T>();
        }

        if (collection.ValueKind != JsonValueKind.Array)
        {
            throw new LibreNmsApiException(
                $"Expected a list in the '{collectionProperty}' field of the response from {relativeUrl}.");
        }

        try
        {
            var items = collection.Deserialize<List<T>>(LibreNmsJson.Options);
            return items ?? (IReadOnlyList<T>)Array.Empty<T>();
        }
        catch (JsonException ex)
        {
            throw new LibreNmsApiException(
                $"Could not read the '{collectionProperty}' data returned by LibreNMS.",
                innerException: ex);
        }
    }

    private static JsonDocument ParseEnvelope(HttpStatusCode statusCode, string content, string relativeUrl)
    {
        JsonDocument? document = null;
        string? serverMessage = null;
        string? envelopeStatus = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                document = JsonDocument.Parse(content);

                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("message", out var messageElement)
                        && messageElement.ValueKind == JsonValueKind.String)
                    {
                        serverMessage = messageElement.GetString();
                    }

                    if (document.RootElement.TryGetProperty("status", out var statusElement)
                        && statusElement.ValueKind == JsonValueKind.String)
                    {
                        envelopeStatus = statusElement.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                document?.Dispose();
                document = null;
            }
        }

        if (!IsSuccess(statusCode))
        {
            document?.Dispose();
            throw new LibreNmsApiException(
                DescribeHttpFailure(statusCode, relativeUrl),
                statusCode,
                serverMessage);
        }

        if (document is null)
        {
            throw new LibreNmsApiException(
                $"LibreNMS returned a response that was not JSON for {relativeUrl}. Check that the address points at LibreNMS and not at a login or proxy page.",
                statusCode);
        }

        if (envelopeStatus is not null && !envelopeStatus.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            document.Dispose();
            throw new LibreNmsApiException(
                serverMessage ?? $"LibreNMS reported an error for {relativeUrl}.",
                statusCode,
                serverMessage);
        }

        return document;
    }

    private static bool IsSuccess(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 200 && code < 300;
    }

    private static string DescribeHttpFailure(HttpStatusCode statusCode, string relativeUrl) => statusCode switch
    {
        HttpStatusCode.Unauthorized => "LibreNMS rejected the API token.",
        HttpStatusCode.Forbidden => "The API token does not have permission for this action.",
        HttpStatusCode.NotFound => $"LibreNMS has no endpoint at '{relativeUrl}'. Check the server address and that the API is enabled.",
        HttpStatusCode.BadGateway => "The LibreNMS server is not responding (bad gateway).",
        HttpStatusCode.ServiceUnavailable => "The LibreNMS server is unavailable.",
        _ => $"LibreNMS returned HTTP {(int)statusCode} for '{relativeUrl}'.",
    };

    private static string DescribeTransportFailure(HttpRequestException ex)
    {
        var inner = ex.InnerException;

        if (inner is System.Security.Authentication.AuthenticationException
            || inner?.GetType().Name.Contains("Certificate", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "The server's TLS certificate was not trusted. Tick 'Allow untrusted certificate' if this is an internal CA or self-signed host.";
        }

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "The server name could not be resolved. Check the address.",
            HttpRequestError.ConnectionError => "Could not connect to the server. Check the address, port and that it is reachable from this machine.",
            HttpRequestError.SecureConnectionError => "The TLS handshake failed. If the certificate is self-signed, tick 'Allow untrusted certificate'.",
            _ => $"Could not reach LibreNMS: {ex.Message}",
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }
}

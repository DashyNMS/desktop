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
    private readonly ServerFailover _failover;
    private readonly object _sync = new();

    private HttpClient? _http;
    private HttpMessageHandler? _handler;
    private LibreNmsConnection? _connection;
    private bool _disposed;

    /// <param name="failover">The backup address state - shared with the app, which shows it and switches back; a throwaway transport (a connection test) gets its own.</param>
    public LibreNmsTransport(ILogger<LibreNmsTransport> logger, ServerFailover? failover = null)
    {
        _logger = logger;
        _failover = failover ?? new ServerFailover();
        _failover.Changed += OnFailoverChanged;
    }

    public ServerFailover Failover => _failover;

    /// <summary>
    /// Whether a struggling or unreachable server gets the usual few retries
    /// with growing waits (see <see cref="TransientRetryPolicy"/>). Off for a
    /// connection test, which should answer quickly - one try at the server
    /// address, then one at the backup - rather than retry an address that
    /// isn't answering for a minute or more first.
    /// </summary>
    public bool RetryTransientFailures { get; init; } = true;

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
    /// <param name="startOnBackup">Dial the backup address from the start - the main one was unreachable when signing in.</param>
    public void Configure(LibreNmsConnection connection, bool startOnBackup = false)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Install(connection);
        _failover.Configure(connection.BackupWebRoot?.ToString(), startOnBackup);
        _logger.LogInformation(
            "LibreNMS transport configured for {ApiBase}{Backup}",
            connection.ApiBase,
            connection.BackupWebRoot is { } backup ? $" (backup address {backup}{(startOnBackup ? ", in use" : string.Empty)})" : string.Empty);
    }

    /// <summary>Switched to the backup address or back: a fresh client, so no pooled connection to the old address carries on being used.</summary>
    private void OnFailoverChanged(object? sender, EventArgs e)
    {
        var connection = Connection;
        if (connection is null)
        {
            return;
        }

        Install(connection);
        if (_failover.IsOnBackup)
        {
            _logger.LogWarning("{Host} stopped answering - now using the backup address {Backup}", connection.WebRoot.Host, _failover.BackupAddress);
        }
        else
        {
            _logger.LogInformation("Switched back to the main address for {Host}", connection.WebRoot.Host);
        }
    }

    private void Install(LibreNmsConnection connection)
    {
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

        // On a backup that's just another route to the server (same scheme,
        // port and path - see LibreNmsConnection.BackupIsAnotherRoute), dial
        // its host in place of the URL's. The request itself still names the
        // server, so TLS (SNI and the certificate check) and the Host header
        // stay as they are. Any other backup is simply its own URL (below).
        var onBackup = _failover.IsOnBackup && connection.BackupWebRoot is not null;
        var dialBackupHost = onBackup && connection.BackupIsAnotherRoute;
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var host = dialBackupHost ? connection.BackupWebRoot!.DnsSafeHost : context.DnsEndPoint.Host;
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(host, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        if (connection.AllowUntrustedCertificate)
        {
            // Opt-in only: many LibreNMS installs sit behind an internal CA.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = onBackup && !dialBackupHost ? connection.BackupApiBase : connection.ApiBase,
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

    public Task<JsonDocument> SendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body = null,
        CancellationToken cancellationToken = default)
        => TrackReachabilityAsync(() => SendWithRetriesAsync(method, relativeUrl, body, cancellationToken));

    public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default)
        => TrackReachabilityAsync(() => SendRawWithRetriesAsync(relativeUrl, cancellationToken));

    /// <summary>
    /// Counts each request, once its own retries are spent, towards the
    /// backup address failover (see <see cref="ServerFailover"/>): an answer of
    /// any kind shows the server's reachable; not reaching it at all counts
    /// against it. The request that tips it over to the backup is tried
    /// again there straight away, so its caller never sees the switch.
    /// </summary>
    private async Task<T> TrackReachabilityAsync<T>(Func<Task<T>> send)
    {
        try
        {
            var result = await send().ConfigureAwait(false);
            _failover.RecordSuccess();
            return result;
        }
        catch (LibreNmsApiException ex) when (ex.StatusCode is not null)
        {
            _failover.RecordSuccess();
            throw;
        }
        catch (LibreNmsApiException ex) when (ServerFailover.IsUnreachable(ex) && _failover.RecordUnreachable())
        {
            var result = await send().ConfigureAwait(false);
            _failover.RecordSuccess();
            return result;
        }
    }

    private async Task<JsonDocument> SendWithRetriesAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken cancellationToken)
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
            // Beyond the immediate retry above, a genuinely struggling server
            // (502/503/504, or the connection/timeout layer failing outright)
            // gets a few more attempts with growing, jittered delays instead
            // of an instant hard failure - see TransientRetryPolicy for what
            // counts as transient and why only GET/PUT are eligible.
            catch (LibreNmsApiException ex) when (
                RetryTransientFailures
                && attempt <= TransientRetryPolicy.MaxAttempts
                && TransientRetryPolicy.IsRetryable(method)
                && TransientRetryPolicy.IsTransientFailure(ex)
                && !cancellationToken.IsCancellationRequested)
            {
                var delay = TransientRetryPolicy.ComputeBackoffDelay(attempt);
                _logger.LogDebug(
                    ex,
                    "Retrying {Url} after a transient failure (attempt {Attempt}/{Max}), waiting {DelayMs:0}ms",
                    relativeUrl,
                    attempt,
                    TransientRetryPolicy.MaxAttempts,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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

    private async Task<string> SendRawWithRetriesAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        // Same retry shape as SendAsync above - see its own comments for why.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendRawOnceAsync(relativeUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (LibreNmsApiException ex) when (attempt == 1 && ex.LooksLikeStaleConnection && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Retrying {Url} after a possible stale pooled connection", relativeUrl);
            }
            catch (LibreNmsApiException ex) when (
                RetryTransientFailures
                && attempt <= TransientRetryPolicy.MaxAttempts
                && TransientRetryPolicy.IsRetryable(HttpMethod.Get)
                && TransientRetryPolicy.IsTransientFailure(ex)
                && !cancellationToken.IsCancellationRequested)
            {
                var delay = TransientRetryPolicy.ComputeBackoffDelay(attempt);
                _logger.LogDebug(
                    ex,
                    "Retrying {Url} after a transient failure (attempt {Attempt}/{Max}), waiting {DelayMs:0}ms",
                    relativeUrl,
                    attempt,
                    TransientRetryPolicy.MaxAttempts,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> SendRawOnceAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        HttpClient http;
        lock (_sync)
        {
            http = _http ?? throw new LibreNmsApiException("Not connected to a LibreNMS server.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);

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

            if (!IsSuccess(response.StatusCode))
            {
                throw new LibreNmsApiException(DescribeHttpFailure(response.StatusCode, relativeUrl), response.StatusCode);
            }

            return content;
        }
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

    private string DescribeTransportFailure(HttpRequestException ex)
    {
        var allowUntrusted = Connection?.AllowUntrustedCertificate == true;

        // Our side rejecting the server's certificate - the one case ticking
        // "Allow untrusted" helps, so only then (and only if it isn't ticked).
        if (IsCertificateRejection(ex) && !allowUntrusted)
        {
            return "The server's TLS certificate was not trusted. Tick 'Allow untrusted certificate' if this is an internal CA or self-signed host.";
        }

        // The far end refusing the handshake (a TLS alert) is something else:
        // whatever answered at that address won't talk TLS for this name -
        // alert 112 is "unrecognised name". A certificate setting won't fix it.
        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError || FindInner<System.Security.Authentication.AuthenticationException>(ex) is not null)
        {
            var alert = System.Text.RegularExpressions.Regex.Match(ex.ToString(), @"TLS alert: '(\d+)'");
            var detail = alert.Success
                ? alert.Groups[1].Value switch
                {
                    "112" => " (TLS alert 112: it doesn't recognise this server name)",
                    "40" => " (TLS alert 40: handshake failure)",
                    "70" => " (TLS alert 70: protocol version)",
                    var code => $" (TLS alert {code})",
                }
                : string.Empty;
            return $"The secure connection couldn't be set up{detail}. Something answered at this address, but not as this LibreNMS server - check the address, or that the server is reachable from here.";
        }

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "The server name could not be resolved. Check the address.",
            HttpRequestError.ConnectionError => "Could not connect to the server. Check the address, port and that it is reachable from this machine.",
            _ => $"Could not reach LibreNMS: {ex.Message}",
        };
    }

    /// <summary>This machine rejected the server's certificate (untrusted issuer, wrong name, expired) - as opposed to the server refusing the handshake.</summary>
    private static bool IsCertificateRejection(Exception ex) =>
        FindInner<System.Security.Authentication.AuthenticationException>(ex) is { } auth
        && auth.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase);

    private static T? FindInner<T>(Exception ex) where T : Exception
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _failover.Changed -= OnFailoverChanged;
        Clear();
    }
}

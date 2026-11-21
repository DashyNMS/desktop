using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>
/// DashyNMS's read-only tools as an MCP server on the loopback interface
/// (#316), for agents to reach through their supported Streamable HTTP MCP
/// configuration. It speaks only what MCP needs - one JSON-RPC POST, one JSON
/// answer - on 127.0.0.1 and a port the system picks.
/// </summary>
/// <remarks>
/// A plain socket rather than HttpListener: on Windows that needs a URL
/// reservation to listen on 127.0.0.1 without administrator rights. Every
/// request needs a live <see cref="AgentToolSession"/>'s bearer token; a Host
/// other than this loopback address, or any browser Origin, is refused, so a
/// web page can't reach it through DNS rebinding. Bodies, headers and idle
/// connections are bounded.
/// </remarks>
public sealed class AgentToolBridge : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly IIntelligenceTools _tools;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<AgentToolSession, byte> _sessions = new();
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _listener;
    private Uri? _endpoint;

    public AgentToolBridge(IIntelligenceTools tools, ILogger logger)
    {
        _tools = tools;
        _logger = logger;
    }

    /// <summary>A new session with its own token, starting the server on first use.</summary>
    public async Task<AgentToolSession> OpenSessionAsync(CancellationToken cancellationToken)
    {
        var endpoint = await StartAsync(cancellationToken).ConfigureAwait(false);
        var session = new AgentToolSession(this, _tools, endpoint, _logger);
        _sessions[session] = 0;
        return session;
    }

    internal void Remove(AgentToolSession session) => _sessions.TryRemove(session, out _);

    /// <summary>Sessions still open - for tests and diagnostics.</summary>
    internal int SessionCount => _sessions.Count;

    private async Task<Uri> StartAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _endpoint) is { } running) return running;
        await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_endpoint is { } started) return started;
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _listener = listener;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptAsync(listener, _stopping.Token);
            Volatile.Write(ref _endpoint, new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/mcp"));
            return _endpoint;
        }
        finally
        {
            _starting.Release();
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeAsync(client, stopping);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new RequestReader(stream);
            while (!stopping.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                idle.CancelAfter(IdleTimeout);
                var request = await reader.ReadAsync(idle.Token).ConfigureAwait(false);
                if (request is null) return;

                var response = await RespondAsync(request, stopping).ConfigureAwait(false);
                await response.WriteAsync(stream, stopping).ConfigureAwait(false);
                if (!request.KeepAlive || response.Close) return;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The agent hung up, went idle or the app is closing.
        }
        catch (BadRequestException)
        {
            // Malformed or over a limit: the connection is closed rather than read further.
        }
    }

    private async Task<Response> RespondAsync(Request request, CancellationToken stopping)
    {
        if (!IsLoopbackHost(request.Header("host")) || request.Header("origin") is not null)
        {
            return Response.Status(403, "Forbidden");
        }

        if (!string.Equals(request.Path, "/mcp", StringComparison.Ordinal))
        {
            return Response.Status(404, "Not Found");
        }

        if (request.Method != "POST")
        {
            // No server-to-agent stream and no session to end: tools only answer requests.
            return Response.Status(405, "Method Not Allowed", ("Allow", "POST"));
        }

        if (FindSession(request.Header("authorization")) is not { } session)
        {
            return Response.Status(401, "Unauthorized", ("WWW-Authenticate", "Bearer"));
        }

        JsonNode? body;
        try
        {
            body = JsonNode.Parse(request.Body);
        }
        catch (JsonException)
        {
            return Response.Json(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = null,
                ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" },
            });
        }

        switch (body)
        {
            case JsonObject message:
                var answer = await session.HandleAsync(message, stopping).ConfigureAwait(false);
                return answer is null ? Response.Status(202, "Accepted") : Response.Json(answer);

            // Batches were dropped from MCP in 2025-06-18, but older clients may still send one.
            case JsonArray batch:
                var answers = new JsonArray();
                foreach (var item in batch.OfType<JsonObject>())
                {
                    if (await session.HandleAsync(item, stopping).ConfigureAwait(false) is { } reply) answers.Add(reply);
                }

                return answers.Count == 0 ? Response.Status(202, "Accepted") : Response.Json(answers);

            default:
                return Response.Status(400, "Bad Request");
        }
    }

    private bool IsLoopbackHost(string? host)
    {
        if (host is null || _endpoint is not { } endpoint) return false;
        var port = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        return string.Equals(host, "127.0.0.1:" + port, StringComparison.Ordinal)
               || string.Equals(host, "localhost:" + port, StringComparison.OrdinalIgnoreCase);
    }

    private AgentToolSession? FindSession(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var presented = Encoding.UTF8.GetBytes(authorization[prefix.Length..].Trim());
        AgentToolSession? found = null;

        // Every session compared in full, so timing says nothing about which tokens exist.
        foreach (var session in _sessions.Keys)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(session.Token))) found = session;
        }

        return found;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var session in _sessions.Keys.ToList()) session.Dispose();
        _listener?.Stop();
        _logger.LogDebug("Intelligence: the agent tool bridge stopped");
    }

    private sealed class BadRequestException : Exception;

    private sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body, bool KeepAlive)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    private sealed record Response(int Code, string Reason, byte[] Body, string? ContentType, (string Name, string Value)[] Extra, bool Close = false)
    {
        public static Response Status(int code, string reason, params (string, string)[] extra) => new(code, reason, Array.Empty<byte>(), null, extra, Close: code >= 400 && code != 401 && code != 405);

        public static Response Json(JsonNode body) => new(200, "OK", Encoding.UTF8.GetBytes(body.ToJsonString()), "application/json", Array.Empty<(string, string)>());

        public async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
        {
            var head = new StringBuilder()
                .Append("HTTP/1.1 ").Append(Code.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason).Append("\r\n")
                .Append("Content-Length: ").Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("Cache-Control: no-store\r\n");
            if (ContentType is not null) head.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            foreach (var (name, value) in Extra) head.Append(name).Append(": ").Append(value).Append("\r\n");
            if (Close) head.Append("Connection: close\r\n");
            head.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
            if (Body.Length > 0) await stream.WriteAsync(Body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads HTTP/1.1 requests off one connection: Content-Length or chunked bodies, within the limits.</summary>
    private sealed class RequestReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public async Task<Request?> ReadAsync(CancellationToken cancellationToken)
        {
            var head = await ReadHeadAsync(cancellationToken).ConfigureAwait(false);
            if (head is null) return null;

            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new BadRequestException();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) throw new BadRequestException();
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            byte[] body;
            if (headers.TryGetValue("transfer-encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                body = await ReadChunkedAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (headers.TryGetValue("content-length", out var lengthText))
            {
                if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > MaxBodyBytes) throw new BadRequestException();
                body = await ReadExactAsync(length, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                body = Array.Empty<byte>();
            }

            var keepAlive = requestLine[2] == "HTTP/1.1"
                ? !(headers.TryGetValue("connection", out var connection) && connection.Equals("close", StringComparison.OrdinalIgnoreCase))
                : headers.TryGetValue("connection", out var keep) && keep.Equals("keep-alive", StringComparison.OrdinalIgnoreCase);
            var path = requestLine[1];
            var query = path.IndexOf('?');
            return new Request(requestLine[0], query >= 0 ? path[..query] : path, headers, body, keepAlive);
        }

        private async Task<string?> ReadHeadAsync(CancellationToken cancellationToken)
        {
            var collected = new List<byte>();
            while (true)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return collected.Count == 0 ? null : throw new BadRequestException();
                }

                collected.Add(_buffer[_start++]);
                var n = collected.Count;
                if (n >= 4 && collected[n - 4] == '\r' && collected[n - 3] == '\n' && collected[n - 2] == '\r' && collected[n - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(collected.ToArray(), 0, n - 4);
                }

                if (n > MaxHeaderBytes) throw new BadRequestException();
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new List<byte>();
            while (true)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false)) throw new BadRequestException();
                var b = _buffer[_start++];
                if (b == '\n') return Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r');
                line.Add(b);
                if (line.Count > 256) throw new BadRequestException();
            }
        }

        private async Task<byte[]> ReadChunkedAsync(CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeText = (await ReadLineAsync(cancellationToken).ConfigureAwait(false)).Split(';')[0].Trim();
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0) throw new BadRequestException();
                if (size == 0)
                {
                    // Trailers, if any, end with an empty line.
                    while ((await ReadLineAsync(cancellationToken).ConfigureAwait(false)).Length > 0) { }
                    return body.ToArray();
                }

                if (body.Length + size > MaxBodyBytes) throw new BadRequestException();
                body.Write(await ReadExactAsync(size, cancellationToken).ConfigureAwait(false));
                await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<byte[]> ReadExactAsync(int length, CancellationToken cancellationToken)
        {
            var result = new byte[length];
            var read = 0;
            while (read < length)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false)) throw new BadRequestException();
                var take = Math.Min(length - read, _end - _start);
                Array.Copy(_buffer, _start, result, read, take);
                _start += take;
                read += take;
            }

            return result;
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            return _end > 0;
        }
    }
}

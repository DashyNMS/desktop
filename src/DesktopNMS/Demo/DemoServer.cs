using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Demo;

/// <summary>
/// A stand-in LibreNMS for demo mode on the loopback address, answering from
/// <see cref="DemoFleet"/>'s example network - so the demo runs the
/// real app, transport and all, without touching a real server. Plain HTTP/1.1
/// on a <see cref="TcpListener"/>: HttpListener wants a URL reservation or
/// admin rights, and one connection per request is all this needs.
/// </summary>
internal sealed class DemoServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly DemoFleet _fleet;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();

    public DemoServer(DemoFleet fleet, ILogger logger)
    {
        _fleet = fleet;
        _logger = logger;
    }

    /// <summary>The address to sign in to, e.g. http://127.0.0.1:50123/.</summary>
    public Uri Address { get; private set; } = new("http://127.0.0.1/");

    public void Start()
    {
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Address = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var (method, target, body) = await ReadRequestAsync(stream).ConfigureAwait(false);
            if (method is null)
            {
                return;
            }

            var response = _fleet.Handle(method, target, body);
            if (response.Unhandled)
            {
                _logger.LogDebug("Demo: no example data for {Method} {Target}", method, target);
            }

            var payload = Encoding.UTF8.GetBytes(response.Body);
            var head = string.Create(
                CultureInfo.InvariantCulture,
                $"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Error")}\r\nContent-Type: {response.ContentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Demo: a request failed");
        }
    }

    private static async Task<(string? Method, string Target, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0)
            {
                return (null, string.Empty, string.Empty);
            }

            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        var head = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray());
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var length = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.Parse(p[1].Trim(), CultureInfo.InvariantCulture))
            .FirstOrDefault();

        var bodyStart = headerEnd + 4;
        while (buffer.Count - bodyStart < length)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        var body = length > 0 ? Encoding.UTF8.GetString(buffer.GetRange(bodyStart, Math.Min(length, buffer.Count - bodyStart)).ToArray()) : string.Empty;
        return (requestLine[0], requestLine.Length > 1 ? requestLine[1] : "/", body);
    }

    private static int IndexOfHeaderEnd(List<byte> buffer)
    {
        for (var i = 0; i + 3 < buffer.Count; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}

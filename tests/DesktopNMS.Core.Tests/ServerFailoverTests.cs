using System.Net;
using System.Net.Sockets;
using System.Text;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class ServerFailoverTests
{
    private static LibreNmsApiException Unreachable() =>
        new("failed", innerException: new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.", new SocketException((int)SocketError.HostNotFound)));

    [Fact]
    public void Two_unreachable_requests_in_a_row_switch_to_the_backup()
    {
        var failover = new ServerFailover();
        failover.Configure("192.0.2.20");
        var changes = 0;
        failover.Changed += (_, _) => changes++;

        Assert.False(failover.RecordUnreachable());
        Assert.False(failover.IsOnBackup);
        Assert.True(failover.RecordUnreachable());
        Assert.True(failover.IsOnBackup);
        Assert.NotNull(failover.SwitchedAt);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void An_answer_in_between_starts_the_count_again()
    {
        var failover = new ServerFailover();
        failover.Configure("192.0.2.20");

        failover.RecordUnreachable();
        failover.RecordSuccess();
        Assert.False(failover.RecordUnreachable());
        Assert.False(failover.IsOnBackup);
    }

    [Fact]
    public void Without_a_backup_address_nothing_switches()
    {
        var failover = new ServerFailover();
        failover.Configure(null);

        failover.RecordUnreachable();
        Assert.False(failover.RecordUnreachable());
        Assert.False(failover.IsOnBackup);
    }

    [Fact]
    public void Once_on_the_backup_it_stays_until_switched_back_by_hand()
    {
        var failover = new ServerFailover();
        failover.Configure("192.0.2.20", startOnBackup: true);

        Assert.True(failover.IsOnBackup);
        Assert.False(failover.RecordUnreachable());
        Assert.False(failover.RecordUnreachable());
        Assert.True(failover.IsOnBackup);

        Assert.True(failover.FailBack());
        Assert.False(failover.IsOnBackup);
        Assert.Null(failover.SwitchedAt);
        Assert.False(failover.FailBack());
    }

    [Fact]
    public void Only_getting_no_answer_from_LibreNMS_counts()
    {
        Assert.True(ServerFailover.IsUnreachable(Unreachable()));
        Assert.True(ServerFailover.IsUnreachable(new LibreNmsApiException("timed out", innerException: new TaskCanceledException())));
        Assert.True(ServerFailover.IsUnreachable(new LibreNmsApiException("refused", innerException: new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)))));

        Assert.False(ServerFailover.IsUnreachable(new LibreNmsApiException("server error", HttpStatusCode.InternalServerError)));
        Assert.False(ServerFailover.IsUnreachable(new LibreNmsApiException("Not connected to a LibreNMS server.")));
        // Something else answering at the address that won't do TLS for this name (alert 112) - the backup may well be the real server.
        Assert.True(ServerFailover.IsUnreachable(new LibreNmsApiException("tls", innerException: new HttpRequestException(HttpRequestError.SecureConnectionError, "x", new System.Security.Authentication.AuthenticationException("Authentication failed because the remote party sent a TLS alert: '112'.")))));
    }

    [Theory]
    [InlineData("https://nms.example.com/", "https://192.0.2.20/", true)]
    [InlineData("https://nms.example.com/librenms/", "https://192.0.2.20/librenms/", true)]
    [InlineData("https://nms.example.com/", "http://192.0.2.20/", false)]
    [InlineData("https://nms.example.com/", "https://192.0.2.20:8443/", false)]
    [InlineData("https://nms.example.com/", "https://192.0.2.20/librenms/", false)]
    public void A_backup_that_only_changes_the_host_is_another_route_to_the_server(string server, string backup, bool anotherRoute)
    {
        var connection = new LibreNmsConnection(new Uri(server), "token", backupWebRoot: new Uri(backup));

        Assert.Equal(anotherRoute, connection.BackupIsAnotherRoute);
        Assert.Equal(new Uri(new Uri(backup), "api/v0/"), connection.BackupApiBase);
    }

    [Fact]
    public async Task A_backup_on_another_route_is_dialled_under_the_server_name()
    {
        using var server = new LoopbackLibreNms();
        var failover = new ServerFailover();
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance, failover);
        transport.Configure(new LibreNmsConnection(
            new Uri($"http://librenms.invalid:{server.Port}/"), "token", timeoutSeconds: 5,
            backupWebRoot: new Uri($"http://127.0.0.1:{server.Port}/")));

        // First request: the main address fails (retries and all) - counted once.
        await Assert.ThrowsAsync<LibreNmsApiException>(() => transport.SendAsync(HttpMethod.Get, "system"));
        Assert.False(failover.IsOnBackup);

        // Second: fails again, switches, and is answered through the backup.
        using var document = await transport.SendAsync(HttpMethod.Get, "system");

        Assert.True(failover.IsOnBackup);

        // The request still names the server, not the backup IP.
        Assert.Contains($"Host: librenms.invalid:{server.Port}", server.SingleRequest());
    }

    [Fact]
    public async Task A_backup_with_its_own_path_scheme_or_port_is_used_as_its_own_URL()
    {
        using var server = new LoopbackLibreNms();
        var failover = new ServerFailover();
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance, failover);
        transport.Configure(
            new LibreNmsConnection(
                new Uri($"http://librenms.invalid:{server.Port}/"), "token", timeoutSeconds: 5,
                backupWebRoot: new Uri($"http://127.0.0.1:{server.Port}/librenms/")),
            startOnBackup: true);

        using var document = await transport.SendAsync(HttpMethod.Get, "system");

        var request = server.SingleRequest();
        Assert.StartsWith("GET /librenms/api/v0/system", request);
        Assert.Contains($"Host: 127.0.0.1:{server.Port}", request);
    }

    /// <summary>A tiny HTTP server on loopback playing LibreNMS - every request gets an empty "ok".</summary>
    private sealed class LoopbackLibreNms : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<string> _requests = new();

        public LoopbackLibreNms()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public string SingleRequest()
        {
            lock (_requests)
            {
                return Assert.Single(_requests);
            }
        }

        public void Dispose() => _listener.Stop();

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer);
                    lock (_requests)
                    {
                        _requests.Add(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    const string body = """{"status":"ok","system":[]}""";
                    var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Stopped.
            }
        }
    }
}

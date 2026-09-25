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
        failover.Configure("10.46.2.10");
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
        failover.Configure("10.46.2.10");

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
        failover.Configure("10.46.2.10", startOnBackup: true);

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
    [InlineData("10.46.2.10", true)]
    [InlineData("fe80::1", true)]
    [InlineData("nms-backup.example.net", true)]
    [InlineData("https://10.46.2.10", false)]
    [InlineData("10.46.2.10/api", false)]
    [InlineData("", false)]
    public void Backup_addresses_are_an_IP_or_a_hostname(string address, bool valid)
    {
        Assert.Equal(valid, ServerFailover.IsValidAddress(address));
    }

    [Fact]
    public async Task The_transport_dials_the_backup_address_once_the_main_one_stops_answering()
    {
        // A tiny HTTP server on loopback plays LibreNMS; the main address is
        // a name that never resolves, the backup is 127.0.0.1.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = new List<string>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer);
                lock (served)
                {
                    served.Add(Encoding.ASCII.GetString(buffer, 0, read));
                }

                const string body = """{"status":"ok","system":[]}""";
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });

        var failover = new ServerFailover();
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance, failover);
        transport.Configure(new LibreNmsConnection(new Uri($"http://librenms.invalid:{port}/"), "token", timeoutSeconds: 5, backupAddress: "127.0.0.1"));

        // First request: the main address fails (retries and all) - counted once.
        await Assert.ThrowsAsync<LibreNmsApiException>(() => transport.SendAsync(HttpMethod.Get, "system"));
        Assert.False(failover.IsOnBackup);

        // Second: fails again, switches, and is answered through the backup.
        using var document = await transport.SendAsync(HttpMethod.Get, "system");

        Assert.True(failover.IsOnBackup);
        lock (served)
        {
            // The request still names the server, not the backup IP.
            Assert.Contains($"Host: librenms.invalid:{port}", Assert.Single(served));
        }
    }
}

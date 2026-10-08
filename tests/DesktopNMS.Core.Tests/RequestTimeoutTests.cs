using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class RequestTimeoutTests
{
    [Fact]
    public void A_new_timeout_applies_without_signing_in_again()
    {
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance) { RetryTransientFailures = false };
        transport.Configure(new LibreNmsConnection(new Uri("https://nms.example.net/"), "token", timeoutSeconds: 30, backupWebRoot: new Uri("https://192.0.2.20/")), startOnBackup: true);

        transport.SetTimeout(120);

        Assert.Equal(120, transport.Connection!.TimeoutSeconds);
        Assert.Equal("token", transport.Connection.ApiToken);
        Assert.NotNull(transport.Connection.BackupWebRoot);
        Assert.True(transport.Failover.IsOnBackup);
    }

    [Fact]
    public void The_timeout_never_drops_below_five_seconds()
    {
        var connection = new LibreNmsConnection(new Uri("https://nms.example.net/"), "token").WithTimeout(1);

        Assert.Equal(5, connection.TimeoutSeconds);
    }
}

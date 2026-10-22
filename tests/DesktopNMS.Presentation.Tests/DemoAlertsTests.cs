using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Demo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class DemoAlertsTests
{
    [Fact]
    public async Task Demo_detects_legacy_alerts_and_loads_all_ten_before_devices()
    {
        using var server = new DemoServer(new DemoFleet(), NullLogger.Instance);
        server.Start();
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance);
        using var client = new LibreNmsClient(transport, NullLogger<LibreNmsClient>.Instance);
        client.Connect(new LibreNmsConnection(server.Address, "demo-token"));

        // No device poll is needed to discover or fetch the alerts.
        var alerts = await client.Alerts.ListAsync();

        Assert.Equal(AlertSource.Alert, await client.Alerts.GetSourceAsync());
        Assert.Equal(10, alerts.Count);
        Assert.All(alerts, alert => Assert.Equal(AlertSource.Alert, alert.Source));
        Assert.Equal(10, alerts.Select(a => a.Id).Distinct().Count());
        Assert.Equal(2, alerts.Count(a => a.State == AlertState.Acknowledged));
    }
}

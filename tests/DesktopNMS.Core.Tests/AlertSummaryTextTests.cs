using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AlertSummaryTextTests
{
    [Fact]
    public void Same_severity_and_location_reads_like_the_mockup()
    {
        var (title, body, detail) = AlertSummaryText.Build(new[]
        {
            new AlertSummaryItem(AlertSeverity.Critical, "core-sw-02", "High temperature", "Rack 3"),
            new AlertSummaryItem(AlertSeverity.Critical, "core-sw-03", "High temperature", "Rack 3"),
            new AlertSummaryItem(AlertSeverity.Critical, "edge-fw-01", "Fan failure", "rack 3"),
        });

        Assert.Equal("3 new critical alerts", title);
        Assert.Equal("core-sw-02 — High temperature", body);
        Assert.Equal("+2 more in Rack 3", detail);
    }

    [Fact]
    public void Mixed_severities_lead_with_the_worst_and_drop_the_severity_word()
    {
        var (title, body, detail) = AlertSummaryText.Build(new[]
        {
            new AlertSummaryItem(AlertSeverity.Warning, "ap-lobby-3", "Signal below threshold", "Lobby"),
            new AlertSummaryItem(AlertSeverity.Critical, "core-sw-02", "Device down", "Rack 3"),
        });

        Assert.Equal("2 new alerts", title);
        Assert.Equal("core-sw-02 — Device down", body);
        Assert.Equal("+1 more", detail);
    }

    [Fact]
    public void Devices_without_a_location_get_no_where()
    {
        var (_, _, detail) = AlertSummaryText.Build(new[]
        {
            new AlertSummaryItem(AlertSeverity.Warning, "a", "r", null),
            new AlertSummaryItem(AlertSeverity.Warning, "b", "r", null),
        });

        Assert.Equal("+1 more", detail);
    }
}

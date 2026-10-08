using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AlertCountingTests
{
    private static readonly Alert[] Alerts =
    {
        Alert("critical", AlertState.Active),
        Alert("warning", AlertState.Active),
        Alert("ok", AlertState.Active),
        Alert(null, AlertState.Active),
        Alert("critical", AlertState.Acknowledged),
        Alert("warning", AlertState.Recovered),
    };

    [Fact]
    public void The_default_counts_what_was_always_counted()
    {
        var settings = new NotificationSettings();

        var count = AlertCounting.Count(Alerts, settings.CountFrom, includeAcknowledged: true);

        Assert.Equal(AlertSeverity.Ok, settings.CountFrom);
        Assert.Equal(5, count.Total);
        Assert.True(count.IsCritical);
    }

    [Theory]
    [InlineData(AlertSeverity.Ok, true, 5, 2, 1)]
    [InlineData(AlertSeverity.Ok, false, 4, 1, 1)]
    [InlineData(AlertSeverity.Warning, true, 3, 2, 1)]
    [InlineData(AlertSeverity.Critical, true, 2, 2, 0)]
    [InlineData(AlertSeverity.Critical, false, 1, 1, 0)]
    public void Each_choice_counts_its_severities(AlertSeverity minimum, bool includeAcknowledged, int total, int critical, int warning)
    {
        var count = AlertCounting.Count(Alerts, minimum, includeAcknowledged);

        Assert.Equal(new AlertCount(total, critical, warning), count);
    }

    [Fact]
    public void Critical_only_leaves_warnings_amber_free()
    {
        var count = AlertCounting.Count(new[] { Alert("warning", AlertState.Active) }, AlertSeverity.Critical, includeAcknowledged: true);

        Assert.Equal(0, count.Total);
        Assert.False(count.IsCritical);
    }

    [Fact]
    public void A_stored_choice_it_does_not_know_counts_everything()
    {
        var settings = new NotificationSettings { CountFromName = "Unknown" };

        Assert.Equal(AlertSeverity.Ok, settings.CountFrom);
    }

    private static Alert Alert(string? severity, AlertState state) => new()
    {
        SeverityText = severity,
        StateValue = (int)state,
    };
}

using System.Text.Json;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AlertHistoryTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Local);

    // The shape /api/v0/logs/alertlog/{device} returns, newest first: the
    // current firing, an ack, a recovery and earlier firings, plus another
    // rule's row and one outside the period.
    private const string AlertLogJson = """
    [
      { "id": 120, "rule_id": 5, "device_id": 3, "state": 1, "time_logged": "2026-10-08 11:00:00", "hostname": "core-sw-01" },
      { "id": 118, "rule_id": 5, "device_id": 3, "state": 0, "time_logged": "2026-10-06 09:30:00", "hostname": "core-sw-01" },
      { "id": 117, "rule_id": 5, "device_id": 3, "state": 2, "time_logged": "2026-10-06 09:10:00", "hostname": "core-sw-01" },
      { "id": 116, "rule_id": 5, "device_id": 3, "state": 1, "time_logged": "2026-10-06 09:00:00", "hostname": "core-sw-01" },
      { "id": 110, "rule_id": 9, "device_id": 3, "state": 1, "time_logged": "2026-10-05 08:00:00", "hostname": "core-sw-01" },
      { "id": 104, "rule_id": 5, "device_id": 3, "state": 3, "time_logged": "2026-09-30 08:05:00", "hostname": "core-sw-01" },
      { "id": 103, "rule_id": 5, "device_id": 3, "state": 1, "time_logged": "2026-09-30 08:00:00", "hostname": "core-sw-01" },
      { "id": 40, "rule_id": 5, "device_id": 3, "state": 1, "time_logged": "2026-08-01 08:00:00", "hostname": "core-sw-01" }
    ]
    """;

    private static IReadOnlyList<AlertLogEntry> Log() => JsonSerializer.Deserialize<List<AlertLogEntry>>(AlertLogJson, DesktopNMS.Core.Json.LibreNmsJson.Options)!;

    [Fact]
    public void Counts_earlier_firings_only_not_every_log_row()
    {
        var summary = AlertHistory.Summarise(Log(), ruleId: 5, deviceId: 3, Now, serverTimestampsAreUtc: false);

        Assert.Equal(2, summary.Count);
        Assert.Equal(new DateTime(2026, 10, 6, 9, 0, 0), summary.LastFired);
        Assert.Equal("Fired 2 times in the last 30 days · last 2 days ago", summary.Describe(Now));
    }

    [Fact]
    public void Can_include_the_current_firing()
    {
        var summary = AlertHistory.Summarise(Log(), 5, 3, Now, serverTimestampsAreUtc: false, excludeCurrent: false);

        Assert.Equal(3, summary.Count);
    }

    [Fact]
    public void Nothing_earlier_says_so()
    {
        var summary = AlertHistory.Summarise(Log(), ruleId: 9, deviceId: 3, Now, serverTimestampsAreUtc: false);

        Assert.Equal(0, summary.Count);
        Assert.Equal("No earlier alerts in the last 30 days", summary.Describe(Now));
    }

    [Fact]
    public void Once_reads_as_once()
    {
        var summary = new AlertHistorySummary(1, Now.AddHours(-3), AlertHistory.DefaultPeriod);

        Assert.Equal("Fired once in the last 30 days · last 3h ago", summary.Describe(Now));
    }
}

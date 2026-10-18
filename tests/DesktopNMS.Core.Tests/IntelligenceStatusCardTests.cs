using System.Text.Json;
using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class IntelligenceStatusCardTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 18, 14, 32, 0, TimeSpan.Zero);

    [Fact]
    public void Individual_alert_details_resolve_alert_and_device_markers()
    {
        var snapshot = Assert.Single(IntelligenceStatusCards.FromTool("alert_details", """
            {"status":"ok","data":{"alert":{"alert_id":9960,"state":"active","severity":"critical"},
             "device":{"device_id":169,"hostname":"router-a","status":"up"},
             "rule":{"name":"MPLS Bandwidth","condition":"devices.status = 1"}}}
            """, Observed));
        var refs = IntelligenceInlineReferences.FromSnapshot(snapshot).ToArray();
        Assert.Equal("MPLS Bandwidth", IntelligenceInlineMarkup.Resolve("[[alert:9960]]", refs)!.Name);
        Assert.Equal("Up", IntelligenceInlineMarkup.Resolve("[[device:169]]", refs)!.Status);
        Assert.Contains("Active", refs.Single(r => r.Key == "alert:9960").Detail);
    }

    [Fact]
    public void Alert_rows_keep_device_state_separate_from_alert_severity_and_acknowledgement()
    {
        var cards = IntelligenceStatusCards.FromTool("active_alerts", """
            {"status":"ok","data":{"devices":[{"device_id":12,"hostname":"ups-a","device_status":"up",
              "alerts":[{"rule":"On battery","severity":"critical","state":"acknowledged","age_minutes":72}]}],
              "page":{"total":414,"returned":1}}}
            """, Observed);
        var card = Assert.Single(cards);
        Assert.Equal("Alert status", card.Title);
        Assert.Contains("1 of 414 matching", card.Scope);
        var row = Assert.Single(card.Rows);
        Assert.Equal("Up", row.DeviceStatus);
        Assert.Equal("Critical", row.Status);
        Assert.Contains("Acknowledged", row.Detail);
        Assert.Contains("1h 12m", row.Detail);
        Assert.Equal(12, row.DeviceId);
        Assert.Equal(Observed, card.ObservedAt);
    }

    [Fact]
    public void Missing_status_is_unknown_and_does_not_inherit_alert_severity()
    {
        var card = Assert.Single(IntelligenceStatusCards.FromTool("active_alerts", """
            {"status":"ok","data":{"devices":[{"hostname":"router-a","alerts":[{"rule":"Device offline","severity":"critical"}]}]}}
            """, Observed));
        var row = Assert.Single(card.Rows);
        Assert.Equal("Unknown", row.DeviceStatus);
        Assert.Contains("State unknown", row.Detail);
        Assert.Contains("total unavailable", card.Scope);
    }

    [Fact]
    public void Overview_counts_use_only_explicit_fields_and_do_not_turn_missing_sources_into_zero()
    {
        var cards = IntelligenceStatusCards.FromTool("network_overview", """
            {"status":"ok","data":{"devices":{"total":20,"up":17,"down":2,"disabled":1},"alerts":null}}
            """, Observed);
        var card = Assert.Single(cards);
        Assert.Equal("Device status", card.Title);
        Assert.Equal("17", card.Rows[0].Detail);
        Assert.Equal("Down", card.Rows[1].Status);
        Assert.DoesNotContain(cards, c => c.Title == "Alert status");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"status\":\"error\",\"message\":\"synthetic-private-token\"}")]
    [InlineData("{\"status\":\"ok\",\"data\":{}}")]
    [InlineData("{\"status\":\"ok\",\"data\":{\"window\":{},\"devices\":[]}}")]
    public void Errors_unknown_shapes_and_historical_windows_do_not_create_misleading_status_cards(string output)
        => Assert.Empty(IntelligenceStatusCards.FromTool("active_alerts", output, Observed));

    [Fact]
    public void Explicit_empty_alert_result_is_shown_as_a_verified_empty_snapshot()
    {
        var card = Assert.Single(IntelligenceStatusCards.FromTool("active_alerts", """
            {"status":"ok","data":{"devices":[],"page":{"total":0,"returned":0}}}
            """, Observed));
        Assert.Empty(card.Rows);
        Assert.Contains("0 of 0 matching", card.Scope);
    }

    [Fact]
    public void Cards_are_bounded_and_preserve_the_matching_total()
    {
        var output = JsonSerializer.Serialize(new { status = "ok", truncated = true, data = new {
            devices = Enumerable.Range(1, 100).Select(id => new { device_id = id, hostname = "device-" + id,
                alerts = new[] { new { rule = "Offline", severity = "critical", state = "active" } } }), page = new { total = 900 } } });
        var card = Assert.Single(IntelligenceStatusCards.FromTool("active_alerts", output, Observed));
        Assert.Equal(IntelligenceStatusCards.MaxRows, card.Rows.Count);
        Assert.Contains("of 900 matching", card.Scope);
        Assert.Contains("shortened", card.Scope);
        Assert.Empty(IntelligenceStatusCards.FromTool("search_logs", output, Observed));
    }

    [Fact]
    public void Device_cards_have_real_identity_and_bounded_display_fields()
    {
        var output = JsonSerializer.Serialize(new { status = "ok", data = new { device = new {
            device_id = 42, hostname = new string('x', 400), status = "down", location = "test-location" } } });
        var card = Assert.Single(IntelligenceStatusCards.FromTool("device_health", output, Observed));
        var row = Assert.Single(card.Rows);
        Assert.Equal(42, row.DeviceId);
        Assert.Equal(200, row.Name.Length);
        Assert.Equal("Down", row.Status);
        Assert.Equal("down", row.Tone);
    }
}

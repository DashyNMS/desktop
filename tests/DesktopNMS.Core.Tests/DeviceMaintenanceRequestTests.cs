using System.Text.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class DeviceMaintenanceRequestTests
{
    [Fact]
    public void Optional_fields_are_omitted_when_unset_matching_librenms_defaults()
    {
        // maintenance_device falls back to the device's own display name for
        // an absent title and Carbon::now() for an absent start - omitting
        // both here rather than sending empty strings lets those defaults apply.
        var request = new DeviceMaintenanceRequest { Duration = "1:00" };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("title", out _));
        Assert.False(root.TryGetProperty("notes", out _));
        Assert.False(root.TryGetProperty("start", out _));
        Assert.Equal("1:00", root.GetProperty("duration").GetString());
        Assert.Equal((int)MaintenanceBehavior.SkipAlerts, root.GetProperty("behavior").GetInt32());
    }

    [Fact]
    public void Every_field_serializes_when_set()
    {
        var request = new DeviceMaintenanceRequest
        {
            Title = "Firmware upgrade",
            Notes = "Scheduled by ops",
            Start = "2026-09-20 09:00:00",
            Duration = "2:30",
            Behavior = (int)MaintenanceBehavior.MuteAlerts,
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"title\":\"Firmware upgrade\"", json);
        Assert.Contains("\"notes\":\"Scheduled by ops\"", json);
        Assert.Contains("\"start\":\"2026-09-20 09:00:00\"", json);
        Assert.Contains("\"duration\":\"2:30\"", json);
        Assert.Contains("\"behavior\":2", json);
    }

    [Theory]
    [InlineData(MaintenanceBehavior.SkipAlerts, "Skip alerts")]
    [InlineData(MaintenanceBehavior.MuteAlerts, "Mute alerts")]
    [InlineData(MaintenanceBehavior.RunAlerts, "Run alerts as normal")]
    public void Behaviors_have_a_display_label(MaintenanceBehavior behavior, string expected)
    {
        Assert.Equal(expected, behavior.ToDisplayString());
    }
}

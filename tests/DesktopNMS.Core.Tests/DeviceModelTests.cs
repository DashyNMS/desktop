using System.Text.Json;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class DeviceModelTests
{
    [Fact]
    public void Last_polled_time_and_duration_are_read_as_LibreNMS_sends_them()
    {
        // Shapes checked against a real server: a MySQL DATETIME string and a fractional number of seconds.
        const string json = """{"device_id":7,"last_polled":"2026-10-01 08:12:41","last_polled_timetaken":1.7041959762573}""";

        var device = JsonSerializer.Deserialize<Device>(json, LibreNmsJson.Options)!;

        Assert.Equal(new DateTime(2026, 10, 1, 8, 12, 41), device.LastPolled);
        Assert.Equal(1.704, device.LastPolledTimeTaken!.Value, 3);
        Assert.False(device.AdditionalData?.ContainsKey("last_polled") ?? false);
    }

    [Fact]
    public void A_device_never_polled_has_no_last_polled_time()
    {
        const string json = """{"device_id":7,"last_polled":null,"last_polled_timetaken":null}""";

        var device = JsonSerializer.Deserialize<Device>(json, LibreNmsJson.Options)!;

        Assert.Null(device.LastPolled);
        Assert.Null(device.LastPolledTimeTaken);
    }
}

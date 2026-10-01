using System.Text.Json;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class LibreNmsDateTimeTests
{
    private static readonly DateTime NineUtc = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_Laravel_style_UTC_timestamp_stays_UTC()
    {
        var value = Read("2026-10-01T09:00:00.000000Z");

        Assert.Equal(NineUtc, value);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    [Theory]
    [InlineData("2026-10-01T10:00:00+01:00")]
    [InlineData("2026-10-01T10:00:00+0100")]
    [InlineData("2026-10-01T04:00:00-05:00")]
    public void A_timestamp_with_an_offset_gives_the_same_instant(string text)
    {
        var value = Read(text);

        Assert.Equal(NineUtc, value);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    [Theory]
    [InlineData("2026-10-01 09:00:00")]
    [InlineData("2026-10-01T09:00:00")]
    public void A_MySQL_timestamp_with_no_zone_stays_unspecified(string text)
    {
        var value = Read(text);

        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), value);
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_zoned_timestamp_converts_to_local_time_whatever_the_UTC_setting(bool serverTimestampsAreUtc)
    {
        var local = ServerTime.ToLocal(Read("2026-10-01T09:00:00.000000Z"), serverTimestampsAreUtc);

        Assert.Equal(NineUtc.ToLocalTime(), local);
    }

    [Fact]
    public void Last_polled_in_the_zoned_form_is_not_shifted_twice()
    {
        var device = JsonSerializer.Deserialize<Device>("""{"device_id":7,"last_polled":"2026-10-01T09:00:00.000000Z"}""", LibreNmsJson.Options)!;

        Assert.Equal(NineUtc.ToLocalTime(), ServerTime.ToLocal(device.LastPolled, serverTimestampsAreUtc: true));
    }

    private static DateTime Read(string text)
        => JsonSerializer.Deserialize<DateTime?>(JsonSerializer.Serialize(text), LibreNmsJson.Options)!.Value;
}

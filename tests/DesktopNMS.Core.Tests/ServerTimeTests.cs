using Xunit;

namespace DesktopNMS.Core.Tests;

public class ServerTimeTests
{
    private static readonly DateTime Unzoned = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void An_unzoned_time_from_a_UTC_server_is_converted_to_local()
    {
        var expected = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc).ToLocalTime();

        Assert.Equal(expected, ServerTime.ToLocal(Unzoned, serverTimestampsAreUtc: true));
    }

    [Fact]
    public void An_unzoned_time_from_a_local_time_server_is_left_as_it_is()
    {
        var local = ServerTime.ToLocal(Unzoned, serverTimestampsAreUtc: false);

        Assert.Equal(Unzoned.Ticks, local.Ticks);
        Assert.Equal(DateTimeKind.Local, local.Kind);
    }

    [Fact]
    public void A_time_already_marked_UTC_is_converted_whatever_the_setting()
    {
        var utc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(utc.ToLocalTime(), ServerTime.ToLocal(utc, serverTimestampsAreUtc: false));
        Assert.Equal(utc.ToLocalTime(), ServerTime.ToLocal(utc, serverTimestampsAreUtc: true));
    }

    [Fact]
    public void Null_stays_null()
    {
        Assert.Null(ServerTime.ToLocal((DateTime?)null, serverTimestampsAreUtc: true));
    }

    [Fact]
    public void Age_is_never_negative()
    {
        var future = DateTime.SpecifyKind(DateTime.Now.AddMinutes(5), DateTimeKind.Unspecified);

        Assert.Equal(TimeSpan.Zero, ServerTime.Age(future, serverTimestampsAreUtc: false));
    }

    [Fact]
    public void Age_uses_the_setting()
    {
        var utcNow = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-1), DateTimeKind.Unspecified);

        var age = ServerTime.Age(utcNow, serverTimestampsAreUtc: true);

        Assert.InRange(age, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
    }
}

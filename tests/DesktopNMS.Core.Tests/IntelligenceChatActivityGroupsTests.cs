using System.Globalization;
using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class IntelligenceChatActivityGroupsTests
{
    [Theory]
    [InlineData("2026-10-18", "Today")]
    [InlineData("2026-10-17", "Yesterday")]
    [InlineData("2026-10-12", "This week")]
    [InlineData("2026-10-11", "Last week")]
    [InlineData("2026-10-05", "Last week")]
    [InlineData("2026-10-04", "Earlier this month")]
    [InlineData("2026-09-30", "September")]
    [InlineData("2026-01-01", "January")]
    [InlineData("2025-12-31", "2025")]
    [InlineData("2024-06-01", "2024")]
    public void Older_activity_progresses_from_days_to_weeks_months_and_years(string activity, string expected)
        => Assert.Equal(expected, Group(activity, "2026-10-18").Label);

    [Theory]
    [InlineData("2026-10-31", "2026-11-02", "Last week")]
    [InlineData("2025-12-31", "2026-01-01", "Yesterday")]
    [InlineData("2025-12-29", "2026-01-01", "This week")]
    [InlineData("2025-12-28", "2026-01-01", "Last week")]
    [InlineData("2025-12-01", "2026-01-01", "2025")]
    public void Calendar_boundaries_keep_recent_activity_in_its_recent_bucket(string activity, string today, string expected)
        => Assert.Equal(expected, Group(activity, today).Label);

    [Fact]
    public void Pinned_chats_stay_ahead_of_every_date_bucket()
    {
        var pinned = IntelligenceChatActivityGroups.ForDate(new(2020, 1, 1), new(2026, 10, 18), true);
        Assert.Equal("Pinned", pinned.Label);
        Assert.True(pinned.Order < Group("2026-10-18", "2026-10-18").Order);
    }

    [Fact]
    public void Months_and_years_sort_newest_first()
    {
        var labels = new[] { "2024-01-01", "2026-01-01", "2026-09-01", "2025-01-01", "2026-10-18" }
            .Select(date => Group(date, "2026-10-18")).OrderBy(group => group.Order).Select(group => group.Label);
        Assert.Equal(new[] { "Today", "September", "January", "2025", "2024" }, labels);
    }

    [Fact]
    public void An_open_app_can_reclassify_a_chat_after_midnight()
    {
        Assert.Equal("Today", Group("2026-10-18", "2026-10-18").Label);
        Assert.Equal("Yesterday", Group("2026-10-18", "2026-10-19").Label);
    }

    private static IntelligenceChatActivityGroup Group(string activity, string today)
        => IntelligenceChatActivityGroups.ForDate(DateOnly.Parse(activity), DateOnly.Parse(today), false, CultureInfo.GetCultureInfo("en-GB"));
}

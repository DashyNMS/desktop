using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class GraphTimeRangeTests
{
    [Theory]
    [InlineData(GraphTimeRangePreset.Hour, "-1hour")]
    [InlineData(GraphTimeRangePreset.Day, "-1day")]
    [InlineData(GraphTimeRangePreset.Week, "-1week")]
    [InlineData(GraphTimeRangePreset.Month, "-1month")]
    [InlineData(GraphTimeRangePreset.Year, "-1year")]
    public void Preset_maps_to_the_expected_relative_from_parameter(GraphTimeRangePreset preset, string expected)
    {
        var range = new GraphTimeRange(preset);

        Assert.Equal(expected, range.ToFromParameter());
        Assert.Null(range.ToToParameter());
    }

    [Fact]
    public void Custom_range_maps_from_and_to_as_unix_seconds()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var range = GraphTimeRange.Custom(from, to);

        Assert.Equal(new DateTimeOffset(from).ToUnixTimeSeconds().ToString(), range.ToFromParameter());
        Assert.Equal(new DateTimeOffset(to).ToUnixTimeSeconds().ToString(), range.ToToParameter());
    }

    [Fact]
    public void Same_preset_ranges_are_equal_for_use_as_a_cache_key()
    {
        // GraphsSectionViewModel caches fetched graphs keyed in part by
        // GraphTimeRange (issue #20) - this only works if two separately
        // constructed instances for the same preset compare equal.
        Assert.Equal(GraphTimeRange.LastWeek, new GraphTimeRange(GraphTimeRangePreset.Week));
        Assert.Equal(GraphTimeRange.LastWeek.GetHashCode(), new GraphTimeRange(GraphTimeRangePreset.Week).GetHashCode());
        Assert.NotEqual(GraphTimeRange.LastWeek, GraphTimeRange.LastMonth);
    }
}

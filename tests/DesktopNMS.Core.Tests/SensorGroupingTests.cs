using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class SensorGroupingTests
{
    [Theory]
    // Slash-separated port paths: the whole path is the component.
    [InlineData("1/1/15 Lane 1 RX Power", "1/1/15")]
    [InlineData("1/1/15 Temperature", "1/1/15")]
    [InlineData("Gi1/0/24 TX Power", "Gi1/0/24")]
    // A word (or words) followed by the number identifying which one.
    [InlineData("Disk 9 HAS5310-20T", "Disk 9")]
    [InlineData("Disk 9 Temperature", "Disk 9")]
    [InlineData("PSU 1 Voltage", "PSU 1")]
    [InlineData("Left rear PSU 2 Voltage", "Left rear PSU 2")]
    // The number can be attached to the word.
    [InlineData("PSU1 Current", "PSU1")]
    [InlineData("Sensor1", "Sensor1")]
    public void ExtractGroupKey_takes_the_component_up_to_its_identifying_number(string description, string expected)
        => Assert.Equal(expected, SensorGrouping.ExtractGroupKey(description));

    [Theory]
    // Different units of the same kind of component must not merge.
    [InlineData("Fan 1 Speed", "Fan 2 Speed")]
    [InlineData("1/1/15 Temperature", "1/1/16 Temperature")]
    [InlineData("PSU 1 Voltage", "PSU 2 Voltage")]
    public void ExtractGroupKey_keeps_separate_units_apart(string first, string second)
        => Assert.NotEqual(SensorGrouping.ExtractGroupKey(first), SensorGrouping.ExtractGroupKey(second));

    [Theory]
    // Nothing to correlate on: these group only with an identical description.
    [InlineData("CPU Temperature")]
    [InlineData("Inlet air temperature")]
    public void ExtractGroupKey_falls_back_to_the_whole_description_without_a_number(string description)
        => Assert.Equal(description, SensorGrouping.ExtractGroupKey(description));

    [Fact]
    public void ExtractGroupKey_handles_missing_descriptions()
    {
        Assert.Equal(string.Empty, SensorGrouping.ExtractGroupKey(null));
        Assert.Equal(string.Empty, SensorGrouping.ExtractGroupKey("   "));
    }

    [Theory]
    [InlineData("1/1/15 Lane 1 RX Power", "1/1/15", "Lane 1 RX Power")]
    [InlineData("Disk 9 Temperature", "Disk 9", "Temperature")]
    [InlineData("PSU 1 - Voltage", "PSU 1", "Voltage")]
    public void ExtractMeasurementLabel_strips_the_shared_key(string description, string groupKey, string expected)
        => Assert.Equal(expected, SensorGrouping.ExtractMeasurementLabel(description, groupKey));

    [Fact]
    public void ExtractMeasurementLabel_is_empty_when_the_description_is_only_the_key()
    {
        // A group of identically-named sensors: the class is what tells the
        // rows apart, so there is no measurement suffix to show.
        Assert.Equal(string.Empty, SensorGrouping.ExtractMeasurementLabel("PSU1", "PSU1"));
    }
}

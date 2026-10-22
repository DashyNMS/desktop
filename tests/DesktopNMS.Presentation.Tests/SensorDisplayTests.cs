using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class SensorDisplayTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Unavailable_readings_have_no_value_bar_or_severity(double? reading)
    {
        var sensor = new Sensor { SensorId = 1, Current = reading, LimitHigh = 100, SensorClass = "temperature" };
        var row = new SensorItemViewModel(sensor, "switch.example.net", null, new BandThresholdSettings(), " °C");
        Assert.Null(row.Value);
        Assert.Equal("–", row.ValueText);
        Assert.Equal("No data", row.SeverityText);
        Assert.Null(row.LimitFraction);
        Assert.False(row.HasLimitFraction);
        Assert.Equal(0, row.LimitPercent);
    }

    [Fact]
    public void Unknown_class_uses_its_optional_unit_and_can_recover_from_no_data()
    {
        var sensor = new Sensor { SensorId = 1, SensorClass = "example", Unit = "widgets" };
        var row = new SensorItemViewModel(sensor, "switch.example.net", null, new BandThresholdSettings(), "");
        Assert.Equal("–", row.ValueText);
        row.Update(new Sensor { SensorId = 1, Current = 25, LimitHigh = 100, SensorClass = "example", Unit = "widgets" },
            "switch.example.net", null, new BandThresholdSettings());
        Assert.Equal("25 widgets", row.ValueText);
        Assert.Equal(0.25, row.LimitFraction);
        row.Update(new Sensor { SensorId = 1, Current = 10, SensorClass = "example" }, "switch.example.net", null, new BandThresholdSettings());
        Assert.Equal("10", row.ValueText);
    }
}

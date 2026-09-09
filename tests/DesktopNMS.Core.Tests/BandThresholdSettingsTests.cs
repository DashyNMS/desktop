using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class BandThresholdSettingsTests
{
    private static readonly BandThresholdSettings Temperature = BandThresholdSettings.TemperatureDefaults();

    [Fact]
    public void Ok_inside_the_healthy_band()
    {
        Assert.Equal(AlertSeverity.Ok, Temperature.Evaluate(20));
        Assert.Equal(AlertSeverity.Ok, Temperature.Evaluate(1));
        Assert.Equal(AlertSeverity.Ok, Temperature.Evaluate(59));
    }

    [Fact]
    public void Warning_just_past_either_side()
    {
        Assert.Equal(AlertSeverity.Warning, Temperature.Evaluate(0));
        Assert.Equal(AlertSeverity.Warning, Temperature.Evaluate(-5));
        Assert.Equal(AlertSeverity.Warning, Temperature.Evaluate(60));
        Assert.Equal(AlertSeverity.Warning, Temperature.Evaluate(70));
    }

    [Fact]
    public void Critical_at_the_extremes()
    {
        Assert.Equal(AlertSeverity.Critical, Temperature.Evaluate(-10));
        Assert.Equal(AlertSeverity.Critical, Temperature.Evaluate(-40));
        Assert.Equal(AlertSeverity.Critical, Temperature.Evaluate(75));
        Assert.Equal(AlertSeverity.Critical, Temperature.Evaluate(120));
    }

    [Fact]
    public void Normalise_resets_to_the_given_defaults_when_the_bands_are_out_of_order()
    {
        var settings = new BandThresholdSettings
        {
            LowCritical = 10,
            LowWarning = -5, // below LowCritical: nonsensical
            HighWarning = 60,
            HighCritical = 75,
        };

        var defaults = BandThresholdSettings.FanSpeedDefaults();
        settings.Normalise(defaults);

        Assert.Equal(defaults.LowCritical, settings.LowCritical);
        Assert.Equal(defaults.LowWarning, settings.LowWarning);
        Assert.Equal(defaults.HighWarning, settings.HighWarning);
        Assert.Equal(defaults.HighCritical, settings.HighCritical);
    }

    [Fact]
    public void Normalise_leaves_a_sensible_custom_configuration_alone()
    {
        var settings = new BandThresholdSettings
        {
            LowCritical = 100,
            LowWarning = 200,
            HighWarning = 18000,
            HighCritical = 22000,
        };

        settings.Normalise(BandThresholdSettings.FanSpeedDefaults());

        Assert.Equal(100, settings.LowCritical);
        Assert.Equal(200, settings.LowWarning);
        Assert.Equal(18000, settings.HighWarning);
        Assert.Equal(22000, settings.HighCritical);
    }
}

using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class SignalThresholdSettingsTests
{
    private static readonly SignalThresholdSettings Defaults = new();

    [Fact]
    public void Critical_at_or_below_the_critical_threshold()
    {
        Assert.Equal(AlertSeverity.Critical, Defaults.Evaluate(-80));
        Assert.Equal(AlertSeverity.Critical, Defaults.Evaluate(-95));
    }

    [Fact]
    public void Warning_between_critical_and_warning_thresholds()
    {
        Assert.Equal(AlertSeverity.Warning, Defaults.Evaluate(-70));
        Assert.Equal(AlertSeverity.Warning, Defaults.Evaluate(-75));
    }

    [Fact]
    public void Ok_above_the_warning_threshold_but_below_the_ignore_sentinel()
    {
        Assert.Equal(AlertSeverity.Ok, Defaults.Evaluate(-69));
        Assert.Equal(AlertSeverity.Ok, Defaults.Evaluate(-30));
    }

    [Fact]
    public void Unknown_at_or_beyond_either_sentinel()
    {
        Assert.Equal(AlertSeverity.Unknown, Defaults.Evaluate(0));
        Assert.Equal(AlertSeverity.Unknown, Defaults.Evaluate(50));
        Assert.Equal(AlertSeverity.Unknown, Defaults.Evaluate(-100));
        Assert.Equal(AlertSeverity.Unknown, Defaults.Evaluate(-150));
    }

    [Fact]
    public void Normalise_resets_to_defaults_when_the_bands_are_out_of_order()
    {
        var settings = new SignalThresholdSettings
        {
            WarningThreshold = -20,
            CriticalThreshold = -10, // higher than warning: nonsensical
            IgnoreAtOrAbove = 0,
            IgnoreAtOrBelow = -100,
        };

        settings.Normalise();

        Assert.Equal(-70, settings.WarningThreshold);
        Assert.Equal(-80, settings.CriticalThreshold);
        Assert.Equal(0, settings.IgnoreAtOrAbove);
        Assert.Equal(-100, settings.IgnoreAtOrBelow);
    }
}

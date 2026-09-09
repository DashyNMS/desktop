using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class DbmThresholdSettingsTests
{
    private static readonly DbmThresholdSettings Defaults = new();

    [Fact]
    public void Critical_at_or_below_the_critical_threshold()
    {
        Assert.Equal(AlertSeverity.Critical, Defaults.Evaluate(-14));
        Assert.Equal(AlertSeverity.Critical, Defaults.Evaluate(-20));
    }

    [Fact]
    public void Warning_between_critical_and_warning_thresholds()
    {
        Assert.Equal(AlertSeverity.Warning, Defaults.Evaluate(-12.5));
        Assert.Equal(AlertSeverity.Warning, Defaults.Evaluate(-13.9));
    }

    [Fact]
    public void Ok_above_the_warning_threshold_but_below_the_ignore_sentinel()
    {
        Assert.Equal(AlertSeverity.Ok, Defaults.Evaluate(-12.4));
        Assert.Equal(AlertSeverity.Ok, Defaults.Evaluate(-6.96));
        Assert.Equal(AlertSeverity.Ok, Defaults.Evaluate(0));
        Assert.Equal(AlertSeverity.Ok, Defaults.Evaluate(38.9));
    }

    [Fact]
    public void Unknown_at_or_above_the_high_ignore_sentinel()
    {
        // Some hardware reports a fixed high value like this for an unplugged
        // port; treated as "no data" rather than flagged as critical.
        Assert.Equal(AlertSeverity.Unknown, Defaults.Evaluate(39));
        Assert.Equal(AlertSeverity.Unknown, Defaults.Evaluate(100));
    }

    [Fact]
    public void Unknown_at_or_below_the_low_ignore_sentinel()
    {
        // Other hardware reports a fixed low value around -39/-40 dBm for the
        // same "no signal" condition instead of a genuinely weak reading, and
        // the exact figure wobbles (e.g. -39.999) rather than landing on a
        // clean -40.0, hence the threshold sitting at -39 rather than -40.
        Assert.Equal(AlertSeverity.Unknown, Defaults.Evaluate(-39));
        Assert.Equal(AlertSeverity.Unknown, Defaults.Evaluate(-39.999));
        Assert.Equal(AlertSeverity.Unknown, Defaults.Evaluate(-99));
    }

    [Fact]
    public void Normalise_resets_to_defaults_when_the_bands_are_out_of_order()
    {
        var settings = new DbmThresholdSettings
        {
            WarningThreshold = -5,
            CriticalThreshold = -1, // higher than warning: nonsensical
            IgnoreAtOrAbove = 39,
            IgnoreAtOrBelow = -39,
        };

        settings.Normalise();

        Assert.Equal(-12.5, settings.WarningThreshold);
        Assert.Equal(-14, settings.CriticalThreshold);
        Assert.Equal(39, settings.IgnoreAtOrAbove);
        Assert.Equal(-39, settings.IgnoreAtOrBelow);
    }

    [Fact]
    public void Normalise_resets_to_defaults_when_the_low_sentinel_is_not_below_critical()
    {
        var settings = new DbmThresholdSettings
        {
            WarningThreshold = -12.5,
            CriticalThreshold = -14,
            IgnoreAtOrAbove = 39,
            IgnoreAtOrBelow = -10, // not below the critical threshold: nonsensical
        };

        settings.Normalise();

        Assert.Equal(-12.5, settings.WarningThreshold);
        Assert.Equal(-14, settings.CriticalThreshold);
        Assert.Equal(39, settings.IgnoreAtOrAbove);
        Assert.Equal(-39, settings.IgnoreAtOrBelow);
    }

    [Fact]
    public void Normalise_leaves_a_sensible_custom_configuration_alone()
    {
        var settings = new DbmThresholdSettings
        {
            WarningThreshold = -10,
            CriticalThreshold = -15,
            IgnoreAtOrAbove = 40,
            IgnoreAtOrBelow = -45,
        };

        settings.Normalise();

        Assert.Equal(-10, settings.WarningThreshold);
        Assert.Equal(-15, settings.CriticalThreshold);
        Assert.Equal(40, settings.IgnoreAtOrAbove);
        Assert.Equal(-45, settings.IgnoreAtOrBelow);
    }
}

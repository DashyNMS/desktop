using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Sensors;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class SensorDefinitionDisplayTests
{
    [Fact]
    public void Labels_keep_raw_graph_values_and_revert_immediately_when_definitions_are_off()
    {
        var sensor = new Sensor { SensorId = 7, DeviceId = 12, SensorClass = "state", Current = 2, LimitHigh = 3,
            StateDefinition = new("Ready", AlertSeverity.Ok, true, "example/fan", "1.0.0") };
        var item = new SensorItemViewModel(sensor, "Switch", null, new UnknownThresholds(), "");
        Assert.Equal("Ready", item.ValueText);
        Assert.Equal(2d, item.Value);
        Assert.Equal("2", item.RawValueText);
        Assert.Contains("Raw value: 2", item.ValueTooltip);
        Assert.Equal(AlertSeverity.Ok, item.Severity);
        Assert.False(item.HasLimitFraction);
        sensor.StateDefinition = null;
        var changes = new List<string?>();
        item.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        item.Update(sensor, "Switch", null, new UnknownThresholds());
        Assert.Equal("2", item.ValueText);
        Assert.Equal(AlertSeverity.Unknown, item.Severity);
        Assert.Equal("Unknown state", item.SeverityText);
        Assert.Contains(nameof(item.ValueText), changes);
    }

    [Fact]
    public void Unresolved_or_label_only_states_do_not_inherit_a_green_threshold()
    {
        var sensor = new Sensor { SensorClass = "state", Current = 2, StateDefinition = SensorStateResolution.Unresolved };
        var item = new SensorItemViewModel(sensor, "Switch", null, new HealthyThresholds(), "");
        Assert.Equal("2", item.ValueText);
        Assert.Equal(AlertSeverity.Unknown, item.Severity);
        sensor.StateDefinition = new("Secondary", AlertSeverity.Unknown, false, "example/role", "1.0.0");
        item.Update(sensor, "Switch", null, new HealthyThresholds());
        Assert.Equal("Secondary", item.ValueText);
        Assert.Equal(AlertSeverity.Unknown, item.Severity);
    }

    [Fact]
    public void Unknown_enum_values_are_explained_without_claiming_the_reading_is_missing()
    {
        var sensor = new Sensor { SensorClass = "state", Current = 99,
            StateDefinition = SensorStateResolution.Unresolved with { Outcome = SensorDefinitionOutcome.UnknownValue } };
        var item = new SensorItemViewModel(sensor, "Example switch", null, new UnknownThresholds(), "");
        Assert.Equal("99", item.ValueText);
        Assert.Equal("Unknown state", item.SeverityText);
        Assert.Contains("not listed", item.ValueTooltip);
        var changes = new List<string?>();
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        sensor.Current = null;
        sensor.StateDefinition = SensorStateResolution.Unresolved with { Outcome = SensorDefinitionOutcome.NoReading };
        item.Update(sensor, "Example switch", null, new UnknownThresholds());
        Assert.Equal("No data", item.SeverityText);
        Assert.Contains(nameof(item.SeverityText), changes);
    }

    private sealed class UnknownThresholds : IThresholdEvaluator { public AlertSeverity Evaluate(double value) => AlertSeverity.Unknown; }
    private sealed class HealthyThresholds : IThresholdEvaluator { public AlertSeverity Evaluate(double value) => AlertSeverity.Ok; }
}

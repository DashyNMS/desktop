using System.Text.Json;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AlertConditionTests
{
    [Fact]
    public void Flat_group_of_leaves_is_editable()
    {
        var json = """
        {"condition":"AND","rules":[
            {"id":"sensors.sensor_class","field":"sensors.sensor_class","type":"string","input":"text","operator":"equal","value":"dbm"},
            {"id":"macros.device_up","field":"macros.device_up","type":"integer","input":"radio","operator":"equal","value":"1"}
        ],"valid":true}
        """;

        var node = JsonSerializer.Deserialize<AlertConditionNode>(json)!;

        Assert.True(node.IsGroup);
        Assert.True(node.IsFlatGroup);
        Assert.Equal(2, node.Rules!.Count);
    }

    [Fact]
    public void Nested_group_is_not_flat()
    {
        var json = """
        {"condition":"AND","rules":[
            {"condition":"OR","rules":[
                {"id":"devices.status","field":"devices.status","type":"integer","input":"radio","operator":"equal","value":"0"}
            ]},
            {"id":"macros.device_up","field":"macros.device_up","type":"integer","input":"radio","operator":"equal","value":"1"}
        ],"valid":true}
        """;

        var node = JsonSerializer.Deserialize<AlertConditionNode>(json)!;

        Assert.True(node.IsGroup);
        Assert.False(node.IsFlatGroup);
    }

    [Fact]
    public void A_leaf_condition_round_trips_through_serialization()
    {
        var node = new AlertConditionNode
        {
            Condition = "AND",
            Valid = true,
            Rules = new()
            {
                new AlertConditionNode { Id = "devices.hostname", Field = "devices.hostname", Type = "string", Input = "text", Operator = "equal", Value = "localhost", Valid = true },
            },
        };

        var json = JsonSerializer.Serialize(node);
        var roundTripped = JsonSerializer.Deserialize<AlertConditionNode>(json)!;

        Assert.True(roundTripped.IsFlatGroup);
        Assert.Equal("devices.hostname", roundTripped.Rules![0].Field);
        Assert.Equal("equal", roundTripped.Rules[0].Operator);
        Assert.Equal("localhost", roundTripped.Rules[0].Value);
    }

    [Fact]
    public void Resolve_finds_a_catalog_field_by_name()
    {
        var field = AlertConditionFields.Resolve("devices.hostname");

        Assert.Equal("devices.hostname", field.Field);
        Assert.Equal("string", field.Type);
        Assert.Contains(field.Operators, o => o.Value == "equal");
    }

    [Fact]
    public void Resolve_falls_back_to_a_generic_string_entry_for_an_unknown_field()
    {
        var field = AlertConditionFields.Resolve("some.unrecognized_column");

        Assert.Equal("some.unrecognized_column", field.Field);
        Assert.Equal("string", field.Type);
    }
}

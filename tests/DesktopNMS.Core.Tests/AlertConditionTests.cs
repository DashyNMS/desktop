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

    [Theory]
    // Columns the hand-curated catalog was missing entirely, from tables the
    // LibreNMS "Entities" doc page never mentions.
    [InlineData("devices.uptime")]
    [InlineData("devices.os")]
    [InlineData("devices.serial")]
    [InlineData("ports.ifInErrors")]
    [InlineData("ports.ifInOctets_rate")]
    [InlineData("ports_statistics.ifInDiscards_rate")]
    [InlineData("wireless_sensors.sensor_current")]
    [InlineData("ssl_certificates.valid_to")]
    [InlineData("vminfo.vmwVmState")]
    [InlineData("transceivers.wavelength")]
    [InlineData("eventlog.message")]
    [InlineData("macros.device_up")]
    public void Catalog_contains_every_field_librenms_offers(string field)
    {
        Assert.NotNull(AlertConditionFields.Find(field));
    }

    [Fact]
    public void Catalog_covers_the_whole_device_related_schema()
    {
        var fields = AlertConditionFields.Groups.SelectMany(g => g.Fields).ToList();

        // LibreNMS's own QueryBuilderFilter resolves 115 device-related tables
        // to 1,385 fields; the 116th group here is the macros. A regeneration
        // that silently collapses is the failure this guards against.
        Assert.Equal(116, AlertConditionFields.Groups.Count);
        Assert.Equal(1385, fields.Count);
        Assert.Equal(fields.Count, fields.Select(f => f.Field).Distinct().Count());
    }

    [Fact]
    public void A_datetime_column_is_typed_as_datetime_and_can_be_compared()
    {
        var field = AlertConditionFields.Resolve("devices.last_polled");

        Assert.Equal("datetime", field.Type);
        Assert.Contains(field.Operators, o => o.Value == "less");
        // regex applies to string/number only in LibreNMS's operator config.
        Assert.DoesNotContain(field.Operators, o => o.Value == "regex");
    }

    [Fact]
    public void An_enum_column_becomes_a_radio_field_restricted_to_equal()
    {
        var field = AlertConditionFields.Resolve("devices.authlevel");

        Assert.Equal("integer", field.Type);
        Assert.Equal("radio", field.Input);
        Assert.Equal(new[] { "equal" }, field.Operators.Select(o => o.Value));
        Assert.Equal(new[] { "noAuthNoPriv", "authNoPriv", "authPriv" }, field.Values);
    }

    [Fact]
    public void A_percentage_macro_takes_numeric_operators_rather_than_yes_no()
    {
        var perc = AlertConditionFields.Resolve("macros.device_cpu_avg_perc");
        var yesNo = AlertConditionFields.Resolve("macros.device_up");

        Assert.Equal("text", perc.Input);
        Assert.Contains(perc.Operators, o => o.Value == "greater");

        Assert.Equal("radio", yesNo.Input);
        Assert.Equal(new[] { "1", "0" }, yesNo.Values);
    }

    [Fact]
    public void Time_based_macros_are_excluded_the_way_librenms_excludes_them()
    {
        // QueryBuilderFilter skips /^past_\d+m$/ - they aren't plain
        // field/operator/value comparisons - but keeps macros.now.
        Assert.Null(AlertConditionFields.Find("macros.past_5m"));
        Assert.NotNull(AlertConditionFields.Find("macros.now"));
    }

    [Fact]
    public void Blacklisted_and_non_device_tables_are_excluded()
    {
        Assert.Null(AlertConditionFields.Find("alerts.state"));
        Assert.Null(AlertConditionFields.Find("alert_log.details"));
        Assert.Null(AlertConditionFields.Find("device_group_device.device_id"));

        // Every table except devices drops its own device_id column.
        Assert.Null(AlertConditionFields.Find("ports.device_id"));
        Assert.NotNull(AlertConditionFields.Find("devices.device_id"));
    }
}

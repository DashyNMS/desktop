using DesktopNMS.Core.Alerting;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AlertRuleSqlImporterTests
{
    [Fact]
    public void A_single_comparison_becomes_a_one_leaf_and_group()
    {
        var node = AlertRuleSqlImporter.Parse("devices.status = 0");

        Assert.Equal("AND", node.Condition);
        var leaf = Assert.Single(node.Rules!);
        Assert.Equal("devices.status", leaf.Field);
        Assert.Equal("devices.status", leaf.Id);
        Assert.Equal("equal", leaf.Operator);
        Assert.Equal("0", leaf.ValueList.Single());
        // Type/input come from the catalog so the result matches what the
        // web builder would have stored.
        Assert.Equal("string", leaf.Type);
        Assert.Equal("text", leaf.Input);
    }

    [Fact]
    public void And_binds_tighter_than_or()
    {
        var node = AlertRuleSqlImporter.Parse("devices.status = 0 AND devices.ignore = 0 OR devices.disabled = 1");

        Assert.Equal("OR", node.Condition);
        Assert.Equal(2, node.Rules!.Count);
        Assert.Equal("AND", node.Rules[0].Condition);
        Assert.Equal(2, node.Rules[0].Rules!.Count);
        Assert.Equal("devices.disabled", node.Rules[1].Field);
    }

    [Fact]
    public void Parentheses_nest_groups()
    {
        var node = AlertRuleSqlImporter.Parse("macros.device_up = 1 AND (sensors.sensor_class = 'dbm' OR sensors.sensor_class = 'temperature')");

        Assert.Equal("AND", node.Condition);
        Assert.False(node.IsFlatGroup);
        Assert.Equal("OR", node.Rules![1].Condition);
        Assert.Equal("dbm", node.Rules[1].Rules![0].ValueList.Single());
    }

    [Theory]
    [InlineData("ports.ifName LIKE 'Gi%'", "begins_with", "Gi")]
    [InlineData("ports.ifName LIKE '%Gi%'", "contains", "Gi")]
    [InlineData("ports.ifName LIKE '%Gi'", "ends_with", "Gi")]
    [InlineData("ports.ifName NOT LIKE '%Gi%'", "not_contains", "Gi")]
    [InlineData("ports.ifName LIKE 'Gi'", "equal", "Gi")]
    [InlineData("ports.ifName REGEXP '^Gi[0-9]'", "regex", "^Gi[0-9]")]
    [InlineData("ports.ifName NOT REGEXP '^Gi'", "not_regex", "^Gi")]
    [InlineData("ports.ifName != 'x'", "not_equal", "x")]
    [InlineData("ports.ifName <> 'x'", "not_equal", "x")]
    [InlineData("sensors.sensor_current > 50", "greater", "50")]
    [InlineData("sensors.sensor_current >= 50", "greater_or_equal", "50")]
    [InlineData("sensors.sensor_current < 50", "less", "50")]
    [InlineData("sensors.sensor_current <= -1.5", "less_or_equal", "-1.5")]
    public void Sql_operators_map_to_builder_operators(string sql, string expectedOperator, string expectedValue)
    {
        var leaf = AlertRuleSqlImporter.Parse(sql).Rules!.Single();

        Assert.Equal(expectedOperator, leaf.Operator);
        Assert.Equal(expectedValue, leaf.ValueList.Single());
    }

    [Theory]
    [InlineData("devices.notes IS NULL", "is_null")]
    [InlineData("devices.notes IS NOT NULL", "is_not_null")]
    [InlineData("devices.notes = ''", "is_empty")]
    [InlineData("devices.notes != ''", "is_not_empty")]
    public void Valueless_operators_have_no_value(string sql, string expectedOperator)
    {
        var leaf = AlertRuleSqlImporter.Parse(sql).Rules!.Single();

        Assert.Equal(expectedOperator, leaf.Operator);
        Assert.Empty(leaf.ValueList);
    }

    [Fact]
    public void Between_and_in_carry_array_values()
    {
        var between = AlertRuleSqlImporter.Parse("devices.uptime BETWEEN 100 AND 200").Rules!.Single();
        var notIn = AlertRuleSqlImporter.Parse("devices.os NOT IN ('ios', 'junos')").Rules!.Single();

        Assert.Equal("between", between.Operator);
        Assert.Equal(new[] { "100", "200" }, between.ValueList);
        Assert.Equal("not_in", notIn.Operator);
        Assert.Equal(new[] { "ios", "junos" }, notIn.ValueList);
    }

    [Fact]
    public void Not_is_pushed_down_into_the_operators()
    {
        var node = AlertRuleSqlImporter.Parse("NOT (devices.status = 1 AND devices.ignore = 0)");

        Assert.Equal("OR", node.Condition);
        Assert.Equal("not_equal", node.Rules![0].Operator);
        Assert.Equal("not_equal", node.Rules[1].Operator);
    }

    [Fact]
    public void A_full_select_from_the_rule_list_drops_librenms_join_clauses()
    {
        // Verbatim shape of AlertRule.query on a live server.
        var sql = "SELECT * FROM devices,sensors WHERE (devices.device_id = ? AND devices.device_id = sensors.device_id) AND ((sensors.sensor_class = \"dbm\" AND sensors.sensor_current < -20))";

        var node = AlertRuleSqlImporter.Parse(sql);

        Assert.Equal("AND", node.Condition);
        Assert.Equal(2, node.Rules!.Count);
        Assert.Equal("sensors.sensor_class", node.Rules[0].Field);
        Assert.Equal("dbm", node.Rules[0].ValueList.Single());
        Assert.Equal("less", node.Rules[1].Operator);
        Assert.Equal("-20", node.Rules[1].ValueList.Single());
    }

    [Fact]
    public void Escaped_quotes_inside_strings_survive()
    {
        var leaves = AlertRuleSqlImporter.Parse("devices.sysName = 'it''s' OR devices.hostname = 'a\\'b'").Rules!;

        Assert.Equal("it's", leaves[0].ValueList.Single());
        Assert.Equal("a'b", leaves[1].ValueList.Single());
    }

    [Fact]
    public void Old_format_rules_convert_the_way_the_web_editor_converts_them()
    {
        var node = AlertRuleSqlImporter.ParseOldFormat("%devices.status = \"0\" && %macros.device_up = \"1\"");

        Assert.Equal("AND", node.Condition);
        Assert.Equal("devices.status", node.Rules![0].Field);
        Assert.Equal("0", node.Rules[0].ValueList.Single());
        Assert.Equal("macros.device_up", node.Rules[1].Field);
        // Macro leaves get the catalog's radio typing.
        Assert.Equal("radio", node.Rules[1].Input);
    }

    [Fact]
    public void Old_format_regex_operator_becomes_regex()
    {
        var leaf = AlertRuleSqlImporter.ParseOldFormat("%ports.ifName ~ \"@Gi@\"").Rules!.Single();

        Assert.Equal("regex", leaf.Operator);
        Assert.Equal(".*Gi.*", leaf.ValueList.Single());
    }

    [Theory]
    [InlineData("devices.status =", "Expected a value")]
    [InlineData("devices.status = 1 AND", "Expected a field name")]
    [InlineData("(devices.status = 1", "Expected ')'")]
    [InlineData("devices.status = 1 extra", "Unexpected 'extra'")]
    [InlineData("(ports.ifInOctets_rate + ports.ifOutOctets_rate) >= 5", "Unexpected '+'")]
    [InlineData("SELECT * FROM devices", "no WHERE clause")]
    public void Unparseable_sql_fails_with_a_pointed_message(string sql, string expectedFragment)
    {
        var ex = Assert.Throws<FormatException>(() => AlertRuleSqlImporter.Parse(sql));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void The_imported_tree_serializes_in_librenms_builder_shape()
    {
        var node = AlertRuleSqlImporter.Parse("devices.status = 0");

        var json = System.Text.Json.JsonSerializer.Serialize(node);

        Assert.Contains("\"condition\":\"AND\"", json);
        Assert.Contains("\"id\":\"devices.status\"", json);
        Assert.Contains("\"value\":\"0\"", json);
        Assert.Contains("\"valid\":true", json);
    }
}

using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AlertRuleConditionsTests
{
    [Fact]
    public void Builder_fields_are_extracted_without_their_table_prefix()
    {
        var rule = new AlertRule
        {
            Id = 7,
            Builder = """
            {
              "condition": "AND",
              "rules": [
                { "id": "ports.ifInErrors_delta", "field": "ports.ifInErrors_delta", "operator": "greater", "value": "500" },
                { "id": "ports.ifOperStatus", "field": "ports.ifOperStatus", "operator": "equal", "value": "up" }
              ],
              "valid": true
            }
            """,
        };

        var fields = AlertRuleConditions.ExtractFields(rule);

        Assert.Equal(2, fields.Count);
        Assert.Contains("ifInErrors_delta", fields);
        Assert.Contains("ifOperStatus", fields);
    }

    [Fact]
    public void Nested_condition_groups_are_walked()
    {
        var rule = new AlertRule
        {
            Builder = """
            {
              "condition": "AND",
              "rules": [
                { "field": "devices.status" },
                {
                  "condition": "OR",
                  "rules": [
                    { "field": "ports.ifInErrors" },
                    { "condition": "AND", "rules": [ { "field": "sensors.sensor_current" } ] }
                  ]
                }
              ]
            }
            """,
        };

        var fields = AlertRuleConditions.ExtractFields(rule);

        Assert.Contains("status", fields);
        Assert.Contains("ifInErrors", fields);
        Assert.Contains("sensor_current", fields);
    }

    [Fact]
    public void Legacy_rule_string_is_used_when_there_is_no_builder()
    {
        var rule = new AlertRule
        {
            Rule = "%devices.status = 0 && %macros.device_down = 1",
        };

        var fields = AlertRuleConditions.ExtractFields(rule);

        Assert.Contains("status", fields);
        Assert.Contains("device_down", fields);
    }

    [Fact]
    public void Builder_wins_over_the_legacy_string()
    {
        var rule = new AlertRule
        {
            Builder = """{ "rules": [ { "field": "ports.ifInErrors" } ] }""",
            Rule = "%devices.status = 0",
        };

        var fields = AlertRuleConditions.ExtractFields(rule);

        Assert.Contains("ifInErrors", fields);
        Assert.DoesNotContain("status", fields);
    }

    [Fact]
    public void Unparsable_builder_falls_back_to_the_legacy_string()
    {
        var rule = new AlertRule
        {
            Builder = "not json at all {{{",
            Rule = "%devices.status = 0",
        };

        Assert.Contains("status", AlertRuleConditions.ExtractFields(rule));
    }

    [Fact]
    public void Missing_rule_yields_an_empty_set()
    {
        Assert.Empty(AlertRuleConditions.ExtractFields(null));
        Assert.Empty(AlertRuleConditions.ExtractFields(new AlertRule()));
    }
}

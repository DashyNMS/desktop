using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class DeviceGroupRuleFormatterTests
{
    [Fact]
    public void Null_or_empty_rules_format_to_null()
    {
        Assert.Null(DeviceGroupRuleFormatter.Format(null));
        Assert.Null(DeviceGroupRuleFormatter.Format(string.Empty));
        Assert.Null(DeviceGroupRuleFormatter.Format("   "));
    }

    [Fact]
    public void Invalid_json_formats_to_null_instead_of_throwing()
    {
        Assert.Null(DeviceGroupRuleFormatter.Format("not json"));
    }

    [Fact]
    public void Simple_or_of_two_begins_with_rules_matches_sql_like_style()
    {
        const string rules = """
            {
                "condition": "OR",
                "rules": [
                    { "field": "devices.sysName", "operator": "begins_with", "value": "w-" },
                    { "field": "devices.display", "operator": "begins_with", "value": "w-" }
                ],
                "valid": true
            }
            """;

        Assert.Equal(
            "devices.sysName LIKE 'w-%' OR devices.display LIKE 'w-%'",
            DeviceGroupRuleFormatter.Format(rules));
    }

    [Fact]
    public void Single_rule_group_is_never_wrapped_in_parentheses()
    {
        const string rules = """
            { "condition": "AND", "rules": [ { "field": "devices.sysName", "operator": "begins_with", "value": "g-fw-" } ] }
            """;

        Assert.Equal("devices.sysName LIKE 'g-fw-%'", DeviceGroupRuleFormatter.Format(rules));
    }

    [Fact]
    public void Nested_groups_are_parenthesised_and_joined_by_the_parent_condition()
    {
        const string rules = """
            {
                "condition": "OR",
                "rules": [
                    { "condition": "OR", "rules": [
                        { "field": "devices.display", "operator": "begins_with", "value": "w-ant" },
                        { "field": "devices.display", "operator": "begins_with", "value": "g-ant" }
                    ]},
                    { "field": "devices.os", "operator": "contains", "value": "acme-wireless" }
                ]
            }
            """;

        Assert.Equal(
            "(devices.display LIKE 'w-ant%' OR devices.display LIKE 'g-ant%') OR devices.os LIKE '%acme-wireless%'",
            DeviceGroupRuleFormatter.Format(rules));
    }

    [Theory]
    [InlineData("equal", "devices.os = 'ios'")]
    [InlineData("not_equal", "devices.os != 'ios'")]
    [InlineData("ends_with", "devices.os LIKE '%ios'")]
    [InlineData("not_contains", "devices.os NOT LIKE '%ios%'")]
    public void Common_operators_render_as_expected(string op, string expected)
    {
        var rules = $$"""
            { "condition": "AND", "rules": [ { "field": "devices.os", "operator": "{{op}}", "value": "ios" } ] }
            """;

        Assert.Equal(expected, DeviceGroupRuleFormatter.Format(rules));
    }
}

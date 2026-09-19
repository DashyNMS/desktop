using DesktopNMS.Core.Alerting;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AlertRuleSqlFormatterTests
{
    [Fact]
    public void Renders_the_same_text_as_librenms_rule_list()
    {
        // Verbatim builder from a live rule; expected text is what the
        // LibreNMS web UI's rule list shows for it (toSql(false)).
        var builder = """
        {"condition":"AND","rules":[
            {"id":"devices.sysName","field":"devices.sysName","type":"string","input":"text","operator":"contains","value":"-gass-"},
            {"id":"devices.sysName","field":"devices.sysName","type":"string","input":"text","operator":"contains","value":"-cam-"},
            {"id":"macros.device_up","field":"macros.device_up","type":"integer","input":"radio","operator":"equal","value":"0"}
        ],"valid":true}
        """;

        Assert.Equal(
            "devices.sysName LIKE '%-gass-%' AND devices.sysName LIKE '%-cam-%' AND macros.device_up = 0",
            AlertRuleSqlFormatter.Format(builder));
    }

    [Fact]
    public void Non_numeric_values_are_double_quoted_and_numbers_left_bare()
    {
        var text = AlertRuleSqlFormatter.Format(AlertRuleSqlImporter.Parse("sensors.sensor_class = 'dbm' AND sensors.sensor_current < -20"));

        Assert.Equal("sensors.sensor_class = \"dbm\" AND sensors.sensor_current < -20", text);
    }

    [Fact]
    public void Sub_groups_are_parenthesised_but_the_top_level_is_not()
    {
        var text = AlertRuleSqlFormatter.Format(AlertRuleSqlImporter.Parse("macros.device_up = 1 AND (sensors.sensor_descr LIKE 'Temp%' OR sensors.sensor_descr LIKE '%Inlet')"));

        Assert.Equal("macros.device_up = 1 AND (sensors.sensor_descr LIKE 'Temp%' OR sensors.sensor_descr LIKE '%Inlet')", text);
    }

    [Theory]
    [InlineData("devices.notes IS NULL", "devices.notes IS NULL")]
    [InlineData("devices.notes IS NOT NULL", "devices.notes IS NOT NULL")]
    [InlineData("devices.notes = ''", "devices.notes = ''")]
    [InlineData("devices.notes != ''", "devices.notes != ''")]
    [InlineData("devices.uptime BETWEEN 100 AND 200", "devices.uptime BETWEEN 100 AND 200")]
    [InlineData("devices.os NOT IN ('ios', 'junos')", "devices.os NOT IN (\"ios\", \"junos\")")]
    [InlineData("ports.ifName REGEXP '^Gi'", "ports.ifName REGEXP \"^Gi\"")]
    [InlineData("ports.ifName NOT LIKE 'Gi%'", "ports.ifName NOT LIKE 'Gi%'")]
    public void Every_operator_round_trips_through_import_and_format(string sql, string expected)
    {
        Assert.Equal(expected, AlertRuleSqlFormatter.Format(AlertRuleSqlImporter.Parse(sql)));
    }

    [Fact]
    public void Unparseable_or_empty_builder_formats_as_null()
    {
        Assert.Null(AlertRuleSqlFormatter.Format((string?)null));
        Assert.Null(AlertRuleSqlFormatter.Format(""));
        Assert.Null(AlertRuleSqlFormatter.Format("not json"));
    }
}

using System.Text.Json;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AlertFaultParserTests
{
    /// <summary>
    /// Mirrors the real shape: an alert rule query joins devices and selects
    /// everything, so the matched row carries the whole device record,
    /// credentials included, alongside the port columns the rule tests.
    /// </summary>
    private static AlertLogEntry EntryWithRow(string rowJson)
    {
        var json = $$"""
        {
          "id": 41,
          "rule_id": 7,
          "device_id": 12,
          "state": 1,
          "time_logged": "2026-09-06 09:23:36",
          "details": { "contacts": {}, "rule": [ {{rowJson}} ] }
        }
        """;

        return JsonSerializer.Deserialize<AlertLogEntry>(json, LibreNmsJson.Options)!;
    }

    private const string RealisticRow = """
        {
          "device_id": 12,
          "inserted": "2026-02-13 07:52:16",
          "hostname": "192.0.2.10",
          "sysName": "sw-core-09",
          "ip": "192.0.2.10",
          "community": "BOX105802",
          "authpass": "hunter2",
          "cryptopass": "sekrit",
          "snmpver": "v2c",
          "port": 161,
          "transport": "udp",
          "status": 1,
          "ifName": "Gi0/0/1",
          "ifAlias": "uplink to core",
          "ifInErrors_delta": 4213,
          "ifOutErrors_delta": 0,
          "ifSpeed": 1000000000
        }
        """;

    [Fact]
    public void Credentials_are_never_returned()
    {
        var detail = AlertFaultParser.Parse(EntryWithRow(RealisticRow));

        var fault = Assert.Single(detail.Faults);
        var names = fault.Fields.Select(f => f.Name).ToArray();
        var values = fault.Fields.Select(f => f.Value).ToArray();

        Assert.DoesNotContain("community", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("authpass", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("cryptopass", names, StringComparer.OrdinalIgnoreCase);

        // The values must not leak under some other column name either.
        Assert.DoesNotContain("BOX105802", values);
        Assert.DoesNotContain("hunter2", values);
        Assert.DoesNotContain("sekrit", values);
    }

    [Theory]
    [InlineData("snmp_community")]
    [InlineData("api_token")]
    [InlineData("ssh_password")]
    [InlineData("PrivKey")]
    public void Credential_like_columns_on_other_tables_are_dropped(string column)
    {
        var detail = AlertFaultParser.Parse(EntryWithRow($$"""{ "ifName": "Gi0/1", "{{column}}": "leaked" }"""));

        var fault = Assert.Single(detail.Faults);
        Assert.DoesNotContain("leaked", fault.Fields.Select(f => f.Value));
    }

    [Fact]
    public void Device_configuration_noise_is_dropped()
    {
        var detail = AlertFaultParser.Parse(EntryWithRow(RealisticRow));

        var names = Assert.Single(detail.Faults).Fields.Select(f => f.Name).ToArray();

        Assert.DoesNotContain("inserted", names);
        Assert.DoesNotContain("snmpver", names);
        Assert.DoesNotContain("transport", names);
        Assert.DoesNotContain("ip", names);
        Assert.DoesNotContain("device_id", names);
    }

    [Fact]
    public void Rule_columns_are_marked_and_come_first()
    {
        var ruleFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ifInErrors_delta" };

        var detail = AlertFaultParser.Parse(EntryWithRow(RealisticRow), ruleFields);
        var fault = Assert.Single(detail.Faults);

        var trigger = Assert.Single(fault.TriggerFields);
        Assert.Equal("ifInErrors_delta", trigger.Name);
        Assert.True(trigger.IsTrigger);

        // The tested column is what the reader needs first.
        Assert.Equal("ifInErrors_delta", fault.PrimaryFields[0].Name);
        Assert.Contains(fault.OtherFields, f => f.Name == "ifSpeed");
        Assert.True(detail.RuleConditionKnown);
    }

    [Fact]
    public void Without_the_rule_every_measured_value_is_shown()
    {
        var detail = AlertFaultParser.Parse(EntryWithRow(RealisticRow));
        var fault = Assert.Single(detail.Faults);

        Assert.False(detail.RuleConditionKnown);
        Assert.Empty(fault.TriggerFields);
        Assert.Contains(fault.PrimaryFields, f => f.Name == "ifInErrors_delta");
        Assert.Empty(fault.SecondaryFields);
    }

    [Fact]
    public void Identity_columns_build_the_title()
    {
        var fault = Assert.Single(AlertFaultParser.Parse(EntryWithRow(RealisticRow)).Faults);

        Assert.Contains("Gi0/0/1", fault.Title);
        Assert.Contains("uplink to core", fault.Title);
    }

    [Fact]
    public void Empty_and_null_values_are_omitted()
    {
        var detail = AlertFaultParser.Parse(
            EntryWithRow("""{ "ifName": "Gi0/1", "ifAlias": "", "ifMtu": null, "ifInErrors": 3 }"""));

        var names = Assert.Single(detail.Faults).Fields.Select(f => f.Name).ToArray();

        Assert.DoesNotContain("ifAlias", names);
        Assert.DoesNotContain("ifMtu", names);
        Assert.Contains("ifInErrors", names);
    }

    [Fact]
    public void Missing_details_yield_an_empty_result()
    {
        Assert.False(AlertFaultParser.Parse(null).HasFaults);

        var entry = JsonSerializer.Deserialize<AlertLogEntry>(
            """{ "id": 1, "rule_id": 1, "device_id": 1, "state": 1, "details": null }""",
            LibreNmsJson.Options)!;

        Assert.False(AlertFaultParser.Parse(entry).HasFaults);
    }

    [Fact]
    public void Diff_sections_are_parsed()
    {
        var json = """
        {
          "id": 41, "rule_id": 7, "device_id": 12, "state": 1,
          "details": {
            "rule": [ { "ifName": "Gi0/1", "ifInErrors": 5 } ],
            "diff": {
              "added": [ { "ifName": "Gi0/2", "ifInErrors": 9 } ],
              "resolved": [ { "ifName": "Gi0/3", "ifInErrors": 1 } ]
            }
          }
        }
        """;

        var entry = JsonSerializer.Deserialize<AlertLogEntry>(json, LibreNmsJson.Options)!;
        var detail = AlertFaultParser.Parse(entry);

        Assert.Single(detail.Faults);
        Assert.Contains("Gi0/2", Assert.Single(detail.Added).Title);
        Assert.Contains("Gi0/3", Assert.Single(detail.Resolved).Title);
    }
}

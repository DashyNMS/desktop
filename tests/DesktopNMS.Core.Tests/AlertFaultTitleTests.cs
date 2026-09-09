using System.Text.Json;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// The fault card title has one job: say which interface, sensor or pool
/// faulted. The device is already named above the list.
/// </summary>
public class AlertFaultTitleTests
{
    private static AlertFault ParseSingle(string rowJson)
    {
        var json = $$"""
        { "id": 1, "rule_id": 1, "device_id": 1, "state": 1,
          "details": { "rule": [ {{rowJson}} ] } }
        """;

        var entry = JsonSerializer.Deserialize<AlertLogEntry>(json, LibreNmsJson.Options)!;
        return Assert.Single(AlertFaultParser.Parse(entry).Faults);
    }

    [Fact]
    public void The_interface_wins_over_the_device()
    {
        var fault = ParseSingle("""
            {
              "hostname": "192.0.2.10",
              "sysName": "sw-core-09",
              "ifName": "Gi0/0/1",
              "ifAlias": "uplink to core",
              "ifInErrors_delta": 4213
            }
            """);

        Assert.Equal("Gi0/0/1 - uplink to core", fault.Title);
    }

    [Fact]
    public void A_sensor_wins_over_the_device()
    {
        var fault = ParseSingle("""
            {
              "hostname": "192.0.2.10",
              "sysName": "sw-core-09",
              "sensor_descr": "PSU 1 temperature",
              "sensor_current": 71.5
            }
            """);

        Assert.Equal("PSU 1 temperature", fault.Title);
    }

    [Fact]
    public void A_device_level_rule_still_gets_a_useful_title()
    {
        // "Device down" rules match the device row itself, so there is no
        // entity to name and the host is the right answer.
        var fault = ParseSingle("""
            { "hostname": "192.0.2.10", "sysName": "sw-core-09", "status": 0 }
            """);

        Assert.Equal("192.0.2.10 - sw-core-09", fault.Title);
    }

    [Fact]
    public void A_row_with_nothing_identifying_does_not_produce_an_empty_title()
    {
        var fault = ParseSingle("""{ "ifInErrors_delta": 12 }""");

        Assert.False(string.IsNullOrWhiteSpace(fault.Title));
    }

    [Fact]
    public void Duplicate_names_are_not_repeated()
    {
        var fault = ParseSingle("""
            { "hostname": "sw-01", "sysName": "sw-01", "status": 0 }
            """);

        Assert.Equal("sw-01", fault.Title);
    }
}

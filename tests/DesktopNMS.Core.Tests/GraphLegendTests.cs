using DesktopNMS.Core.Graphs;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class GraphLegendTests
{
    [Theory]
    [InlineData("device_temperature", "temperature")]
    [InlineData("device_fanspeed", "fanspeed")]
    [InlineData("device_processor", null)]
    [InlineData("device_bits", null)]
    [InlineData("uptime", null)]
    public void Sensor_class_comes_from_the_graph_name(string graph, string? sensorClass)
        => Assert.Equal(sensorClass, GraphLegend.SensorClassOf(graph));

    [Fact]
    public void Sensors_are_one_class_in_description_order_with_units()
    {
        var sensors = new[]
        {
            new Sensor { SensorId = 3, SensorClass = "temperature", Description = "outlet", Current = 43 },
            new Sensor { SensorId = 1, SensorClass = "temperature", Description = "ASIC", Current = 66.25 },
            new Sensor { SensorId = 2, SensorClass = "fanspeed", Description = "Fan 1", Current = 3200 },
            new Sensor { SensorId = 4, SensorClass = "Temperature", Description = "Inlet", Current = 31 },
        };

        var legend = GraphLegend.ForSensors(sensors, "temperature");

        Assert.Equal(new[] { "ASIC", "Inlet", "outlet" }, legend.Select(e => e.Name));
        Assert.Equal(new int?[] { 1, 4, 3 }, legend.Select(e => e.SensorId));
        Assert.Equal("66.3 °C", legend[0].Value);
    }

    [Fact]
    public void Processors_are_in_stored_order()
    {
        var legend = GraphLegend.ForProcessors(new[]
        {
            new ProcessorSensor { ProcessorId = 9, Description = "Core 1", UsagePercent = 41.6 },
            new ProcessorSensor { ProcessorId = 7, Description = "Core 0", UsagePercent = 12 },
        });

        Assert.Equal(new[] { "Core 0", "Core 1" }, legend.Select(e => e.Name));
        Assert.Equal("42%", legend[1].Value);
    }

    [Fact]
    public void Traffic_sums_the_ports_LibreNMS_counts()
    {
        var legend = GraphLegend.ForTraffic(new[]
        {
            new Port { IfName = "Te1/1/1", IfType = "ethernetCsmacd", IfInOctetsRate = 250_000_000, IfOutOctetsRate = 50_000_000 },
            new Port { IfName = "Te1/1/2", IfType = "ethernetCsmacd", IfInOctetsRate = 150_000_000, IfOutOctetsRate = 87_500_000 },
            new Port { IfName = "Lo0", IfType = "softwareLoopback", IfInOctetsRate = 999_000_000 },
            new Port { IfName = "Vlan10", IfType = "propVirtual", IfInOctetsRate = 999_000_000 },
        });

        Assert.Equal(new[] { "In", "Out" }, legend.Select(e => e.Name));
        Assert.Equal("3.2 Gb/s", legend[0].Value);
        Assert.Equal("1.1 Gb/s", legend[1].Value);
    }
}

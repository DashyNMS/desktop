using System.Globalization;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Graphs;

/// <summary>One series in a graph's legend: what it is, and its value now.</summary>
/// <param name="SensorId">For a sensor graph: the sensor, whose own graph can be fetched to fit the scale to it alone.</param>
public sealed record GraphLegendEntry(string Name, string? Detail, string? Value, int? SensorId = null);

/// <summary>
/// The app's own legend for a LibreNMS graph (asked for with "legend=no"):
/// each series' name and current value, in the order LibreNMS draws them -
/// so entry n is the graph's series n (see <see cref="GraphSvgStyle.Restyle"/>).
/// Only for graphs whose series order is known from LibreNMS's own source:
/// a device's sensors of one class (sensor.inc.php: by description), its
/// processors (processor_separate.inc.php: as stored) and its traffic
/// (bits.inc.php: In, then Out). Anything else keeps LibreNMS's own legend.
/// </summary>
public static class GraphLegend
{
    public const string ProcessorGraph = "device_processor";
    public const string TrafficGraph = "device_bits";

    /// <summary>"device_temperature" → "temperature"; null for a graph that isn't a sensor class's.</summary>
    public static string? SensorClassOf(string graphName)
        => graphName.StartsWith("device_", StringComparison.Ordinal) && graphName is not (ProcessorGraph or TrafficGraph) ? graphName["device_".Length..] : null;

    /// <summary>A device's sensors of one class, as LibreNMS orders them: by description.</summary>
    public static IReadOnlyList<GraphLegendEntry> ForSensors(IEnumerable<Sensor> deviceSensors, string sensorClass)
        => deviceSensors
            .Where(s => string.Equals(s.SensorClass, sensorClass, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SensorId)
            .Select(s => new GraphLegendEntry(s.Description ?? $"Sensor {s.SensorId}", null, FormatSensor(s.Current, sensorClass), s.SensorId))
            .ToList();

    /// <summary>A device's processors, in the order they're stored - as LibreNMS draws them.</summary>
    public static IReadOnlyList<GraphLegendEntry> ForProcessors(IEnumerable<ProcessorSensor> processors)
        => processors
            .OrderBy(p => p.ProcessorId)
            .Select(p => new GraphLegendEntry(p.Description ?? $"Processor {p.ProcessorId}", null, p.UsagePercent is { } usage ? $"{usage:0}%" : null))
            .ToList();

    /// <summary>
    /// In and Out, each the sum across the ports LibreNMS counts towards a
    /// device's traffic - leaving out loopbacks, tunnels, VLAN and LAG
    /// interfaces and the like, as its default device_traffic_iftype and
    /// device_traffic_descr settings do.
    /// </summary>
    public static IReadOnlyList<GraphLegendEntry> ForTraffic(IEnumerable<Port> ports)
    {
        var counted = ports.Where(p => !p.Deleted && !p.Disabled && !p.Ignore && !IsLeftOutOfTraffic(p)).ToList();
        var inBits = counted.Sum(p => p.IfInOctetsRate ?? 0) * 8;
        var outBits = counted.Sum(p => p.IfOutOctetsRate ?? 0) * 8;
        return new[]
        {
            new GraphLegendEntry("In", null, FormatRate(inBits)),
            new GraphLegendEntry("Out", null, FormatRate(outBits)),
        };
    }

    /// <summary>
    /// A port graph's series (named by <see cref="GraphSeriesNames"/>, in
    /// legend order) with the port's current values where LibreNMS reports
    /// them: traffic, unicast packets and errors - not broadcast, multicast
    /// or discards, which the API's port rates leave out.
    /// </summary>
    public static IReadOnlyList<GraphLegendEntry> ForPort(string graphType, IReadOnlyList<string> names, Port port)
        => names.Select(name => new GraphLegendEntry(name, null, (graphType, name) switch
        {
            ("port_bits", "In") => Format(port.IfInOctetsRate, v => FormatRate(v * 8)),
            ("port_bits", "Out") => Format(port.IfOutOctetsRate, v => FormatRate(v * 8)),
            ("port_upkts", "In") => Format(port.IfInUcastPktsRate, FormatPerSecond),
            ("port_upkts", "Out") => Format(port.IfOutUcastPktsRate, FormatPerSecond),
            ("port_errors", "Errors in") => Format(port.IfInErrorsRate, FormatPerSecond),
            ("port_errors", "Errors out") => Format(port.IfOutErrorsRate, FormatPerSecond),
            _ => null,
        })).ToList();

    private static string? Format(double? value, Func<double, string> format) => value is { } v ? format(v) : null;

    /// <summary>Packets or errors a second: "0.02/s", "840/s", "1.2k/s".</summary>
    public static string FormatPerSecond(double perSecond)
    {
        string[] units = ["", "k", "M", "G"];
        var value = perSecond;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        var number = value >= 100 ? value.ToString("0", CultureInfo.InvariantCulture)
            : value >= 1 || unit > 0 ? value.ToString("0.#", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
        return number + units[unit] + "/s";
    }

    public static string FormatRate(double bitsPerSecond)
    {
        string[] units = ["b/s", "kb/s", "Mb/s", "Gb/s", "Tb/s"];
        var value = bitsPerSecond;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(value >= 100 || unit == 0 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture))} {units[unit]}");
    }

    public static string FormatSensor(double value, string sensorClass)
    {
        var unit = sensorClass.ToLowerInvariant() switch
        {
            "temperature" => " °C",
            "humidity" or "charge" or "load" or "percent" or "loss" => "%",
            "fanspeed" => " RPM",
            "voltage" => " V",
            "current" => " A",
            "power" or "cooling" => " W",
            "power_consumed" => " kWh",
            "frequency" => " Hz",
            "runtime" => " min",
            "dbm" or "signal" => " dBm",
            "snr" or "quality_factor" => " dB",
            "pressure" => " kPa",
            "airflow" => " cfm",
            "delay" => " s",
            "waterflow" => " l/m",
            "tv_signal" => " dBmV",
            "bitrate" => " bps",
            _ => string.Empty,
        };

        var number = Math.Abs(value) >= 100 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);
        return number + unit;
    }

    private static readonly Regex LeftOutType = new("loopback|tunnel|virtual|mpls|ieee8023adLag|l2vlan|ppp", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LeftOutName = new("loopback|vlan|tunnel|bond|null|dummy", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsLeftOutOfTraffic(Port port)
        => (port.IfType is { } type && LeftOutType.IsMatch(type))
           || (port.IfDescr is { } descr && LeftOutName.IsMatch(descr))
           || (port.IfName is { } name && LeftOutName.IsMatch(name));
}

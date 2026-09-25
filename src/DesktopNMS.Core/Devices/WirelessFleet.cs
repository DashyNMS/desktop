using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Devices;

/// <summary>One wireless controller's line on the Wireless dashboard widget (#55).</summary>
public sealed record WirelessControllerSummary(int DeviceId, double? ApCount, double? Clients, AlertSeverity Severity)
{
    public bool HasAny => ApCount is not null || Clients is not null;
}

/// <summary>
/// Works out which devices the Wireless dashboard widget should ask about,
/// and sums up what each reports. LibreNMS has no fleet-wide wireless
/// listing - only a per-device one - and asking every device on every
/// refresh would be hundreds of calls for the handful that have radios. So
/// the widget first asks one live device of each OS (<see cref="ProbeCandidates"/>),
/// then only ever polls the devices of the OSes that answered with wireless
/// readings (<see cref="DevicesToPoll"/>). Which readings a device has
/// comes from its OS's discovery module, so one device of an OS speaks for
/// the rest.
/// </summary>
public static class WirelessFleet
{
    /// <summary>One device per OS to ask - the lowest-numbered one that's up and polled - skipping ping-only devices, which never have SNMP data.</summary>
    public static IReadOnlyList<Device> ProbeCandidates(IEnumerable<Device> devices, IReadOnlySet<string> alreadyProbed)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(alreadyProbed);

        return devices
            .Where(d => d.Status && !d.Disabled && !string.IsNullOrWhiteSpace(d.Os) && !IsPingOnly(d.Os!) && !alreadyProbed.Contains(d.Os!))
            .GroupBy(d => d.Os!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(d => d.DeviceId).First())
            .OrderBy(d => d.Os, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every enabled device of an OS known to report wireless readings, down ones included - a controller that's gone down is the thing to notice.</summary>
    public static IReadOnlyList<Device> DevicesToPoll(IEnumerable<Device> devices, IReadOnlySet<string> wirelessOses)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(wirelessOses);

        return devices
            .Where(d => !d.Disabled && d.Os is { } os && wirelessOses.Contains(os))
            .OrderBy(d => d.DeviceId)
            .ToList();
    }

    /// <summary>
    /// A device's AP and client counts - each summed over the device's
    /// readings of that class, as LibreNMS's own "sum" aggregator does (a
    /// controller may report clients per radio band) - and the worst state
    /// any of its wireless readings is in against its own LibreNMS limits.
    /// </summary>
    public static WirelessControllerSummary Summarise(int deviceId, IEnumerable<WirelessSensor> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);

        var live = sensors.Where(s => !s.Deleted).ToList();

        static double? Sum(IEnumerable<WirelessSensor> of)
        {
            var values = of.Where(s => s.Current is not null).Select(s => s.Current!.Value).ToList();
            return values.Count > 0 ? values.Sum() : null;
        }

        var severity = live
            .Select(Evaluate)
            .DefaultIfEmpty(AlertSeverity.Unknown)
            .Max();

        return new WirelessControllerSummary(
            deviceId,
            Sum(live.Where(s => s.HasClass(WirelessSensorClasses.ApCount))),
            Sum(live.Where(s => s.HasClass(WirelessSensorClasses.Clients))),
            severity);
    }

    /// <summary>A reading against the limits LibreNMS has for it - the same rules as the Health tab applies to a sensor's own limits. Unknown without a reading.</summary>
    public static AlertSeverity Evaluate(WirelessSensor sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        if (sensor.Current is not { } value)
        {
            return AlertSeverity.Unknown;
        }

        var limits = new ThresholdBounds(sensor.LimitLow, sensor.LimitLowWarn, sensor.LimitHighWarn, sensor.LimitHigh);
        return new HybridThresholdEvaluator(default, limits, overrideSensorLimitsWithAppThresholds: false).Evaluate(value);
    }

    private static bool IsPingOnly(string os) => string.Equals(os, "ping", StringComparison.OrdinalIgnoreCase);
}

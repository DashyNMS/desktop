using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Devices;

/// <summary>Which direction a Top widget ranks by.</summary>
public enum RankBy
{
    Total,
    In,
    Out,
}

/// <summary>One port in a Top list: its rates in bits (or errors) per second, both ways.</summary>
public sealed record RankedPort(Port Port, double In, double Out)
{
    public double Total => In + Out;
}

/// <summary>One device in the Top devices list: its ports' traffic summed, both ways, and how many ports are carrying any.</summary>
public sealed record RankedDevice(int DeviceId, double In, double Out, int ActivePorts)
{
    public double Total => In + Out;
}

/// <summary>
/// The rankings behind the Top interfaces, Top errors and Top devices
/// dashboard widgets (#198-#201), over every port in the fleet. Pure
/// functions over LibreNMS's own rates, so mobile can use the same ones.
/// </summary>
public static class PortRankings
{
    /// <summary>The busiest ports by traffic, in bits per second (LibreNMS's octet rates times 8). Ports carrying nothing are left out.</summary>
    public static IReadOnlyList<RankedPort> TopTraffic(IEnumerable<Port> ports, RankBy by, int count)
    {
        ArgumentNullException.ThrowIfNull(ports);

        return Rank(
            ports.Select(p => new RankedPort(p, Bits(p.IfInOctetsRate), Bits(p.IfOutOctetsRate))),
            by,
            count,
            includeZero: false);
    }

    /// <summary>
    /// The ports with the most errors per second. <paramref name="includeZero"/>
    /// keeps error-free ports in (a full list); otherwise a healthy network
    /// gives an empty one.
    /// </summary>
    public static IReadOnlyList<RankedPort> TopErrors(IEnumerable<Port> ports, RankBy by, int count, bool includeZero = false)
    {
        ArgumentNullException.ThrowIfNull(ports);

        return Rank(
            ports.Select(p => new RankedPort(p, Positive(p.IfInErrorsRate), Positive(p.IfOutErrorsRate))),
            by,
            count,
            includeZero);
    }

    /// <summary>
    /// The devices moving the most traffic, summed over their ports in bits
    /// per second. A device passing traffic through counts it on both ports,
    /// so this is "total port traffic", not throughput.
    /// </summary>
    public static IReadOnlyList<RankedDevice> TopDevices(IEnumerable<Port> ports, RankBy by, int count)
    {
        ArgumentNullException.ThrowIfNull(ports);

        var devices = ports
            .GroupBy(p => p.DeviceId)
            .Select(g =>
            {
                var rates = g.Select(p => (In: Bits(p.IfInOctetsRate), Out: Bits(p.IfOutOctetsRate))).ToList();
                return new RankedDevice(g.Key, rates.Sum(r => r.In), rates.Sum(r => r.Out), rates.Count(r => r.In + r.Out > 0));
            })
            .Where(d => d.Total > 0);

        return OrderBy(devices, d => d.In, d => d.Out, d => d.Total, by)
            .ThenBy(d => d.DeviceId)
            .Take(Math.Max(0, count))
            .ToList();
    }

    private static IReadOnlyList<RankedPort> Rank(IEnumerable<RankedPort> ports, RankBy by, int count, bool includeZero)
    {
        var candidates = includeZero ? ports : ports.Where(p => Value(p, by) > 0);

        return OrderBy(candidates, p => p.In, p => p.Out, p => p.Total, by)
            .ThenBy(p => p.Port.DeviceId)
            .ThenBy(p => p.Port.PortId)
            .Take(Math.Max(0, count))
            .ToList();
    }

    private static IOrderedEnumerable<T> OrderBy<T>(IEnumerable<T> items, Func<T, double> inbound, Func<T, double> outbound, Func<T, double> total, RankBy by) => by switch
    {
        RankBy.In => items.OrderByDescending(inbound).ThenByDescending(total),
        RankBy.Out => items.OrderByDescending(outbound).ThenByDescending(total),
        _ => items.OrderByDescending(total),
    };

    private static double Value(RankedPort port, RankBy by) => by switch
    {
        RankBy.In => port.In,
        RankBy.Out => port.Out,
        _ => port.Total,
    };

    /// <summary>LibreNMS's octet rate (bytes per second) as bits per second; missing or nonsense as 0.</summary>
    private static double Bits(double? bytesPerSecond) => Positive(bytesPerSecond) * 8;

    private static double Positive(double? value) => value is { } v && v > 0 && !double.IsNaN(v) && !double.IsInfinity(v) ? v : 0;
}

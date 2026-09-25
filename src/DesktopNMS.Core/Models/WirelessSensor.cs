using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNMS.Core.Json;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from /api/v0/devices/{id}/wireless-sensors (#55) - one wireless
/// reading LibreNMS keeps apart from its ordinary sensors: a controller's AP
/// and client counts, or a radio link's signal, noise, rate, ... See
/// <see cref="WirelessSensorClasses"/> for what each class means.
/// </summary>
public sealed class WirelessSensor
{
    [JsonPropertyName("sensor_id")]
    public int SensorId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    /// <summary>e.g. "ap-count", "clients", "snr", "noise-floor".</summary>
    [JsonPropertyName("sensor_class")]
    public string? SensorClass { get; set; }

    [JsonPropertyName("sensor_descr")]
    public string? Description { get; set; }

    [JsonPropertyName("sensor_index")]
    [JsonConverter(typeof(LooseStringConverter))]
    public string? Index { get; set; }

    [JsonPropertyName("sensor_current")]
    public double? Current { get; set; }

    [JsonPropertyName("sensor_limit")]
    public double? LimitHigh { get; set; }

    [JsonPropertyName("sensor_limit_warn")]
    public double? LimitHighWarn { get; set; }

    [JsonPropertyName("sensor_limit_low")]
    public double? LimitLow { get; set; }

    [JsonPropertyName("sensor_limit_low_warn")]
    public double? LimitLowWarn { get; set; }

    /// <summary>Soft-deleted by discovery (the reading has gone away) - LibreNMS keeps the row, but it's no longer polled.</summary>
    [JsonPropertyName("sensor_deleted")]
    public bool Deleted { get; set; }

    [JsonPropertyName("lastupdate")]
    public DateTime? LastUpdate { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public bool HasClass(string sensorClass) => string.Equals(SensorClass, sensorClass, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What each LibreNMS wireless sensor class is called and measured in -
/// LibreNMS's own lang/en/wireless.php, which is where its web UI gets them
/// (the API only returns the raw class name).
/// </summary>
public static class WirelessSensorClasses
{
    public const string ApCount = "ap-count";
    public const string Clients = "clients";

    private static readonly Dictionary<string, (string Name, string Unit)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [ApCount] = ("APs", string.Empty),
        [Clients] = ("Clients", string.Empty),
        ["capacity"] = ("Capacity", "%"),
        ["ccq"] = ("Client connection quality", "%"),
        ["errors"] = ("Errors", string.Empty),
        ["error-ratio"] = ("Error ratio", "%"),
        ["error-rate"] = ("Bit error rate", "bps"),
        ["frequency"] = ("Frequency", "MHz"),
        ["distance"] = ("Distance", "m"),
        ["mse"] = ("MSE", "dB"),
        ["mcs"] = ("MCS", string.Empty),
        ["noise-floor"] = ("Noise floor", "dBm"),
        ["power"] = ("Power/signal", "dBm"),
        ["quality"] = ("Quality", "%"),
        ["rate"] = ("Rate", "bps"),
        ["rssi"] = ("RSSI", "dBm"),
        ["snr"] = ("SNR", "dB"),
        ["sinr"] = ("SINR", "dB"),
        ["rsrq"] = ("RSRQ", "dB"),
        ["rsrp"] = ("RSRP", "dBm"),
        ["ssr"] = ("Signal strength ratio", "dB"),
        ["utilization"] = ("Utilisation", "%"),
        ["xpi"] = ("Cross-polar interference", "dB"),
        ["cell"] = ("Cell", string.Empty),
        ["channel"] = ("Channel", string.Empty),
    };

    /// <summary>"APs", "Noise floor" - or the class itself, tidied, for one this app doesn't know yet.</summary>
    public static string NameOf(string? sensorClass)
    {
        if (string.IsNullOrWhiteSpace(sensorClass))
        {
            return "Other";
        }

        if (Known.TryGetValue(sensorClass, out var known))
        {
            return known.Name;
        }

        var words = sensorClass.Trim().Replace('-', ' ').Replace('_', ' ');
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    public static string UnitOf(string? sensorClass) =>
        sensorClass is not null && Known.TryGetValue(sensorClass, out var known) ? known.Unit : string.Empty;

    /// <summary>Orders the classes the way the Wireless section lists them: counts first, then as LibreNMS's own list has them.</summary>
    public static int SortRank(string? sensorClass)
    {
        var index = 0;
        foreach (var key in Known.Keys)
        {
            if (string.Equals(key, sensorClass, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            index++;
        }

        return int.MaxValue;
    }

    /// <summary>"131", "-92 dBm", "5,180 MHz", "300 Mbps".</summary>
    public static string Format(string? sensorClass, double? value)
    {
        if (value is not { } v)
        {
            return "-";
        }

        var unit = UnitOf(sensorClass);
        if (unit == "bps")
        {
            return Bits(v);
        }

        var number = Math.Abs(v % 1) < 0.0001
            ? v.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
            : v.ToString("N1", System.Globalization.CultureInfo.CurrentCulture);

        return unit.Length == 0 ? number : unit == "%" ? number + "%" : number + " " + unit;
    }

    /// <summary>The graph LibreNMS draws for a class, one line per reading of it on the device: "device_wireless_clients".</summary>
    public static string GraphName(string sensorClass) => "device_wireless_" + sensorClass;

    /// <summary>The class a wireless graph is for - the reverse of <see cref="GraphName"/> - or null for any other graph.</summary>
    public static string? ClassOfGraph(string? graphName) =>
        graphName is not null && graphName.StartsWith("device_wireless_", StringComparison.Ordinal) && graphName.Length > "device_wireless_".Length
            ? graphName["device_wireless_".Length..]
            : null;

    private static string Bits(double bps)
    {
        string[] units = ["bps", "kbps", "Mbps", "Gbps", "Tbps"];
        var value = Math.Abs(bps);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        var text = value.ToString(value >= 100 || unit == 0 ? "N0" : "0.#", System.Globalization.CultureInfo.CurrentCulture);
        return (bps < 0 ? "-" : string.Empty) + text + " " + units[unit];
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from /api/v0/resources/sensors - one reading from one sensor on one
/// device (dBm optical power, temperature, voltage, ...).
/// </summary>
public sealed class Sensor
{
    [JsonPropertyName("sensor_id")]
    public int SensorId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    /// <summary>The kind of sensor, e.g. "dbm", "temperature", "voltage".</summary>
    [JsonPropertyName("sensor_class")]
    public string? SensorClass { get; set; }

    [JsonPropertyName("sensor_descr")]
    public string? Description { get; set; }

    /// <summary>Which reading of its kind on the device this is, e.g. "hpicfPseAvailablePower.1001" - what pairs a PoE budget's total with its used figure (see <see cref="Alerting.PoeBudget"/>).</summary>
    [JsonPropertyName("sensor_index")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseStringConverter))]
    public string? Index { get; set; }

    [JsonPropertyName("sensor_current")]
    public double Current { get; set; }

    /// <summary>LibreNMS's own high-critical limit, if one is configured.</summary>
    [JsonPropertyName("sensor_limit")]
    public double? LimitHigh { get; set; }

    /// <summary>LibreNMS's own high-warning limit, if one is configured.</summary>
    [JsonPropertyName("sensor_limit_warn")]
    public double? LimitHighWarn { get; set; }

    /// <summary>LibreNMS's own low-critical limit, if one is configured.</summary>
    [JsonPropertyName("sensor_limit_low")]
    public double? LimitLow { get; set; }

    /// <summary>LibreNMS's own low-warning limit, if one is configured.</summary>
    [JsonPropertyName("sensor_limit_low_warn")]
    public double? LimitLowWarn { get; set; }

    [JsonPropertyName("lastupdate")]
    public DateTime? LastUpdate { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public bool HasClass(string sensorClass) => string.Equals(SensorClass, sensorClass, StringComparison.OrdinalIgnoreCase);

    public bool IsDbm => HasClass("dbm");
}

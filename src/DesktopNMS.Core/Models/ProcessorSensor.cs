using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One CPU/processor reading from /api/v0/devices/{id}/health/processor/{sensor_id}.
/// Unlike <see cref="Sensor"/>, LibreNMS has no fleet-wide endpoint for these -
/// only a per-device list of ids, then one call per id for the actual reading.
/// </summary>
public sealed class ProcessorSensor
{
    [JsonPropertyName("processor_id")]
    public int ProcessorId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("processor_descr")]
    public string? Description { get; set; }

    [JsonPropertyName("processor_usage")]
    public double? UsagePercent { get; set; }

    /// <summary>LibreNMS's own configured warning threshold for this processor, if set.</summary>
    [JsonPropertyName("processor_perc_warn")]
    public double? WarningPercent { get; set; }
}

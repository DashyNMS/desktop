using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One memory pool reading from /api/v0/devices/{id}/health/mempool/{sensor_id}
/// (e.g. "Physical memory", "Virtual memory", "Cached memory"). Fetched the
/// same two-step way as <see cref="ProcessorSensor"/> and <see cref="StorageVolume"/> -
/// see their remarks.
/// </summary>
public sealed class MempoolSensor
{
    [JsonPropertyName("mempool_id")]
    public int MempoolId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("mempool_descr")]
    public string? Description { get; set; }

    [JsonPropertyName("mempool_perc")]
    public double? UsagePercent { get; set; }

    /// <summary>LibreNMS's own configured warning threshold for this pool, if set.</summary>
    [JsonPropertyName("mempool_perc_warn")]
    public double? WarningPercent { get; set; }

    [JsonPropertyName("mempool_used")]
    public long? UsedBytes { get; set; }

    [JsonPropertyName("mempool_free")]
    public long? FreeBytes { get; set; }

    [JsonPropertyName("mempool_total")]
    public long? TotalBytes { get; set; }
}

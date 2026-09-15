using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One disk/filesystem reading from /api/v0/devices/{id}/health/storage/{sensor_id}
/// (e.g. "/", "/var/log"). Fetched the same two-step way as
/// <see cref="ProcessorSensor"/> and <see cref="MempoolSensor"/> - see their remarks.
/// </summary>
public sealed class StorageVolume
{
    [JsonPropertyName("storage_id")]
    public int StorageId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("storage_descr")]
    public string? Description { get; set; }

    [JsonPropertyName("storage_perc")]
    public double? UsagePercent { get; set; }

    /// <summary>LibreNMS's own configured warning threshold for this volume, if set.</summary>
    [JsonPropertyName("storage_perc_warn")]
    public double? WarningPercent { get; set; }

    [JsonPropertyName("storage_used")]
    public long? UsedBytes { get; set; }

    [JsonPropertyName("storage_free")]
    public long? FreeBytes { get; set; }

    [JsonPropertyName("storage_size")]
    public long? TotalBytes { get; set; }
}

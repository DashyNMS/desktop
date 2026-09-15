using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One row from /api/v0/devices/{id}/availability - the device's uptime
/// percentage over a fixed window. LibreNMS always returns four rows (1 day,
/// 7 days, 30 days, 1 year), identified by <see cref="DurationSeconds"/>
/// rather than a label.
/// </summary>
public sealed class AvailabilityWindow
{
    [JsonPropertyName("duration")]
    public long DurationSeconds { get; set; }

    [JsonPropertyName("availability_perc")]
    public double Percent { get; set; }
}

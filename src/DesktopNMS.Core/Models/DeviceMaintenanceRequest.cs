using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// POST body for /api/v0/devices/{id}/maintenance (see <see cref="Api.IDevicesApi.ScheduleMaintenanceAsync"/>),
/// shaped against LibreNMS's <c>maintenance_device</c> handler rather than
/// the docs.
/// </summary>
/// <remarks>
/// <see cref="Duration"/> is the one field the handler actually requires -
/// everything else has a server-side fallback: an absent <see cref="Title"/>
/// becomes the device's own display name, an absent <see cref="Start"/>
/// starts the window immediately (<c>Carbon::now()</c>), and an absent or
/// invalid <see cref="Behavior"/> falls back to the server's configured
/// default. All of that is timezone-naive on both ends, the same as every
/// other timestamp this app reads from LibreNMS
/// (<see cref="Json.LibreNmsDateTimeConverter"/>) - <see cref="Start"/> is
/// the server's own wall-clock time, not UTC.
/// </remarks>
public sealed class DeviceMaintenanceRequest
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }

    /// <summary>"yyyy-MM-dd HH:mm:00" in the server's own time; null starts the window immediately.</summary>
    [JsonPropertyName("start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Start { get; set; }

    /// <summary>Required by the server - "H:mm", e.g. "2:00" for two hours, "0:30" for thirty minutes.</summary>
    [JsonPropertyName("duration")]
    public string Duration { get; set; } = "1:00";

    [JsonPropertyName("behavior")]
    public int Behavior { get; set; } = (int)MaintenanceBehavior.SkipAlerts;
}

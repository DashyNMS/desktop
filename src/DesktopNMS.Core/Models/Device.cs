using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from /api/v0/devices. Present so the alert list can be enriched and so
/// a device view can be added without reworking the client.
/// </summary>
public sealed class Device
{
    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("sysName")]
    public string? SysName { get; set; }

    [JsonPropertyName("display")]
    public string? Display { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("os")]
    public string? Os { get; set; }

    [JsonPropertyName("hardware")]
    public string? Hardware { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    /// <summary>1 = up, 0 = down.</summary>
    [JsonPropertyName("status")]
    public bool Status { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("ignore")]
    public bool Ignore { get; set; }

    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    [JsonIgnore]
    public string BestName =>
        !string.IsNullOrWhiteSpace(Display) ? Display! :
        !string.IsNullOrWhiteSpace(SysName) ? SysName! :
        !string.IsNullOrWhiteSpace(Hostname) ? Hostname! :
        $"device {DeviceId}";

    /// <summary>
    /// The single state to show for this device. Disabled and ignored both take
    /// priority over the raw up/down reading, since a disabled device's stale
    /// "status" value says nothing about whether it is actually reachable.
    /// </summary>
    [JsonIgnore]
    public DeviceState State =>
        Disabled ? DeviceState.Disabled :
        Ignore ? DeviceState.Ignored :
        Status ? DeviceState.Up :
        DeviceState.Down;

    public override string ToString() => BestName;
}

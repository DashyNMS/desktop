using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>A distributed-poller group, as used by <see cref="Api.AddDeviceRequest.PollerGroup"/>.</summary>
public sealed class PollerGroup
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("group_name")]
    public string GroupName { get; set; } = string.Empty;

    [JsonPropertyName("descr")]
    public string? Descr { get; set; }
}

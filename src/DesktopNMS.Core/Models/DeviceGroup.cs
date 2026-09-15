using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>A row from /api/v0/devicegroups - one of the fleet's own saved device groups (dynamic or static).</summary>
public sealed class DeviceGroup
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string? Description { get; set; }
}

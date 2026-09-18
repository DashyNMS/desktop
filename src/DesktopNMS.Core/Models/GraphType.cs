using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One entry from /api/v0/devices/{id}/graphs - a device-wide graph this
/// device has available (e.g. "device_uptime", "Poller Time"). The actual
/// image is fetched separately via /api/v0/devices/{id}/{Name} - see
/// <see cref="Api.IGraphsApi.GetSvgAsync"/>.
/// </summary>
public sealed class GraphType
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Description { get; set; } = string.Empty;
}

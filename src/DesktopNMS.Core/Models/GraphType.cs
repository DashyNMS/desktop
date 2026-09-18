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

    /// <summary>
    /// The shared ComboBox template's closed/selected display falls back to
    /// this rather than honouring DisplayMemberPath (a pre-existing WPF
    /// quirk, not new here) - every other object bound to a ComboBox in
    /// this app (PollerGroup, DeviceNameOption, ...) already works around
    /// it the same way.
    /// </summary>
    public override string ToString() => Description;
}

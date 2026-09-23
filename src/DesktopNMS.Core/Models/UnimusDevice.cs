using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A device as Unimus knows it - the result of
/// <c>GET devices/findByAddress/{address}</c>. Only the fields this app
/// actually uses are modelled; Unimus's own device object carries more
/// (schedule, connections, zone) that aren't needed here.
/// </summary>
public sealed class UnimusDevice
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("lastJobStatus")]
    public string? LastJobStatus { get; set; }
}

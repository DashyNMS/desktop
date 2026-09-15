using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One row from /api/v0/resources/ip/arp/all?device={id} - an IPv4-to-MAC
/// mapping the device has resolved on one of its ports.
/// </summary>
public sealed class ArpEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("port_id")]
    public int PortId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("mac_address")]
    public string? MacAddress { get; set; }

    [JsonPropertyName("ipv4_address")]
    public string? Ipv4Address { get; set; }
}

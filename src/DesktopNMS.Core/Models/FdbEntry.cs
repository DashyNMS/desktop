using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One row from /api/v0/devices/{id}/fdb - a MAC address the switch has
/// learned on one of its ports, and the VLAN it was seen on.
/// </summary>
public sealed class FdbEntry
{
    [JsonPropertyName("ports_fdb_id")]
    public int Id { get; set; }

    [JsonPropertyName("port_id")]
    public int PortId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("mac_address")]
    public string? MacAddress { get; set; }

    [JsonPropertyName("vlan_id")]
    public int? VlanId { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}

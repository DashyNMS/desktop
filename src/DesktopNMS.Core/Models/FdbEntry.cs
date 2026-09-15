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

    /// <summary>
    /// LibreNMS's own internal id for the VLAN row, NOT the 802.1Q VLAN
    /// number - confirmed against a live server, where this held values like
    /// 50 on a device whose actual VLANs were 1/2074/2076/2099/2102/2254.
    /// Resolve the real tag and name via <see cref="Vlan"/> (<see cref="IVlansApi"/>),
    /// keyed by this value.
    /// </summary>
    [JsonPropertyName("vlan_id")]
    public int? VlanId { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}

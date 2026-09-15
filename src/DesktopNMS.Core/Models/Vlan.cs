using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One row from /api/v0/resources/vlans - a VLAN LibreNMS knows about on some
/// device. <see cref="VlanId"/> is the internal id other endpoints reference
/// (e.g. <see cref="FdbEntry.VlanId"/>); <see cref="VlanNumber"/> is the
/// actual 802.1Q tag a human would recognise.
/// </summary>
public sealed class Vlan
{
    [JsonPropertyName("vlan_id")]
    public int VlanId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("vlan_vlan")]
    public int VlanNumber { get; set; }

    [JsonPropertyName("vlan_name")]
    public string? VlanName { get; set; }
}

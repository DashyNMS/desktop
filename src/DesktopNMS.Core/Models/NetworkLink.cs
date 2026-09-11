using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from /api/v0/devices/{id}/links - a layer-2 neighbour relationship
/// LibreNMS discovered via LLDP/CDP/FDP/etc. between one local port and a
/// port on another device.
/// </summary>
public sealed class NetworkLink
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("local_port_id")]
    public int LocalPortId { get; set; }

    [JsonPropertyName("remote_port_id")]
    public int? RemotePortId { get; set; }

    /// <summary>The remote port's own name/description, as reported by the discovery protocol.</summary>
    [JsonPropertyName("remote_port")]
    public string? RemotePort { get; set; }

    [JsonPropertyName("remote_hostname")]
    public string? RemoteHostname { get; set; }

    /// <summary>Set when the remote device is itself monitored by this LibreNMS instance.</summary>
    [JsonPropertyName("remote_device_id")]
    public int? RemoteDeviceId { get; set; }

    [JsonPropertyName("remote_platform")]
    public string? RemotePlatform { get; set; }

    /// <summary>Discovery protocol: "lldp", "cdp", "fdp", ...</summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    [JsonIgnore]
    public string DisplayRemoteName => !string.IsNullOrWhiteSpace(RemoteHostname) ? RemoteHostname! : "unknown device";

    public override string ToString() => $"{DisplayRemoteName} ({RemotePort})";
}

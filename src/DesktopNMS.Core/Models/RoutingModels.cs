using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A BGP session (LibreNMS's <c>bgpPeers</c> table, from <c>GET bgp?hostname=</c>)
/// - columns as in LibreNMS's own database schema.
/// </summary>
public sealed class BgpSession
{
    [JsonPropertyName("bgpPeer_id")]
    public int Id { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    /// <summary>The peer's address - stored uncompressed for IPv6, see <see cref="PeerAddressText"/>.</summary>
    [JsonPropertyName("bgpPeerIdentifier")]
    public string? PeerIdentifier { get; set; }

    [JsonPropertyName("bgpPeerRemoteAs")]
    public long RemoteAs { get; set; }

    /// <summary>The remote AS's name, where LibreNMS could look it up.</summary>
    [JsonPropertyName("astext")]
    public string? AsText { get; set; }

    /// <summary>idle, connect, active, opensent, openconfirm or established.</summary>
    [JsonPropertyName("bgpPeerState")]
    public string? State { get; set; }

    /// <summary>start or stop (running or halted on some platforms).</summary>
    [JsonPropertyName("bgpPeerAdminStatus")]
    public string? AdminStatus { get; set; }

    [JsonPropertyName("bgpPeerDescr")]
    public string? Description { get; set; }

    [JsonPropertyName("bgpLocalAddr")]
    public string? LocalAddress { get; set; }

    [JsonPropertyName("bgpPeerRemoteAddr")]
    public string? RemoteAddress { get; set; }

    [JsonPropertyName("bgpPeerLastErrorText")]
    public string? LastErrorText { get; set; }

    /// <summary>Seconds the session has been established (or was, before it dropped).</summary>
    [JsonPropertyName("bgpPeerFsmEstablishedTime")]
    public long EstablishedSeconds { get; set; }

    [JsonPropertyName("bgpPeerInUpdates")]
    public long InUpdates { get; set; }

    [JsonPropertyName("bgpPeerOutUpdates")]
    public long OutUpdates { get; set; }

    [JsonPropertyName("vrf_id")]
    public int? VrfId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    [JsonIgnore]
    public bool IsEstablished => string.Equals(State, "established", StringComparison.OrdinalIgnoreCase);

    /// <summary>Administratively shut down - "stop" or "halted", however the device spells it.</summary>
    [JsonIgnore]
    public bool IsAdminDown => AdminStatus?.Trim().ToLowerInvariant() is "stop" or "halted";

    [JsonIgnore]
    public string PeerAddressText => RoutingText.Address(PeerIdentifier);
}

/// <summary>An OSPF (v2) neighbour (LibreNMS's <c>ospf_nbrs</c>, from <c>GET ospf?hostname=</c>).</summary>
public sealed class OspfNeighbour
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    /// <summary>The local interface the neighbour is on, when LibreNMS matched one.</summary>
    [JsonPropertyName("port_id")]
    public int? PortId { get; set; }

    [JsonPropertyName("ospfNbrIpAddr")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("ospfNbrRtrId")]
    public string? RouterId { get; set; }

    /// <summary>down, attempt, init, twoWay, exchangeStart, exchange, loading or full.</summary>
    [JsonPropertyName("ospfNbrState")]
    public string? State { get; set; }

    [JsonPropertyName("ospfNbrPriority")]
    public int Priority { get; set; }

    [JsonPropertyName("ospfNbrEvents")]
    public long Events { get; set; }

    [JsonPropertyName("context_name")]
    public string? ContextName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>An OSPFv3 neighbour (LibreNMS's <c>ospfv3_nbrs</c>, from <c>GET ospfv3?hostname=</c>).</summary>
public sealed class Ospfv3Neighbour
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("port_id")]
    public int? PortId { get; set; }

    [JsonPropertyName("ospfv3NbrAddress")]
    public string? Address { get; set; }

    /// <summary>The neighbour's router id - dotted, as LibreNMS stores it in <c>router_id</c>.</summary>
    [JsonPropertyName("router_id")]
    public string? RouterId { get; set; }

    [JsonPropertyName("ospfv3NbrState")]
    public string? State { get; set; }

    [JsonPropertyName("ospfv3NbrPriority")]
    public int Priority { get; set; }

    [JsonPropertyName("ospfv3NbrEvents")]
    public long Events { get; set; }

    [JsonPropertyName("context_name")]
    public string? ContextName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>A VRF on a device (LibreNMS's <c>vrfs</c>, from <c>GET routing/vrf?hostname=</c>). A port's <see cref="Port.IfVrf"/> names the <see cref="Id"/> it belongs to.</summary>
public sealed class Vrf
{
    [JsonPropertyName("vrf_id")]
    public int Id { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("vrf_name")]
    public string? Name { get; set; }

    [JsonPropertyName("mplsVpnVrfRouteDistinguisher")]
    public string? RouteDistinguisher { get; set; }

    [JsonPropertyName("mplsVpnVrfDescription")]
    public string? Description { get; set; }

    [JsonPropertyName("bgpLocalAs")]
    public long? LocalAs { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>Formatting shared by the routing models.</summary>
public static class RoutingText
{
    /// <summary>An address as usually written: LibreNMS stores IPv6 peers uncompressed ("2001:0db8:0000:...:0001"), shown compressed ("2001:db8::1").</summary>
    public static string Address(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return string.Empty;
        }

        var text = stored.Trim();
        return IPAddress.TryParse(text, out var address) ? address.ToString() : text;
    }
}

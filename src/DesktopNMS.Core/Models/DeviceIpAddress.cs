using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One row from /api/v0/devices/{id}/ip - a single IPv4 or IPv6 address bound
/// to one of the device's interfaces. The endpoint mixes both kinds into one
/// array, distinguished by which set of fields is populated rather than a
/// type column of its own.
/// </summary>
public sealed class DeviceIpAddress
{
    [JsonPropertyName("port_id")]
    public int PortId { get; set; }

    [JsonPropertyName("ipv4_address")]
    public string? Ipv4Address { get; set; }

    [JsonPropertyName("ipv4_prefixlen")]
    public int? Ipv4PrefixLength { get; set; }

    [JsonPropertyName("ipv6_address")]
    public string? Ipv6Address { get; set; }

    /// <summary>The zero-compressed form (e.g. "fe80::250:56ff:feb9:804c") - what is worth displaying, unlike the fully expanded <see cref="Ipv6Address"/>.</summary>
    [JsonPropertyName("ipv6_compressed")]
    public string? Ipv6Compressed { get; set; }

    [JsonPropertyName("ipv6_prefixlen")]
    public int? Ipv6PrefixLength { get; set; }

    public bool IsIpv6 => Ipv6Address is not null;

    /// <summary>"192.0.2.1/24" or "fe80::250:56ff:feb9:804c/64", whichever this row actually is.</summary>
    public string DisplayText => IsIpv6
        ? $"{Ipv6Compressed ?? Ipv6Address}/{Ipv6PrefixLength}"
        : $"{Ipv4Address}/{Ipv4PrefixLength}";
}

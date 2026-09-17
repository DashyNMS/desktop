using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Api;

/// <summary>
/// POST /api/v0/devices body (see <see cref="IDevicesApi.AddAsync"/>). Only
/// <see cref="Hostname"/> is required; every other field is left out of the
/// request entirely when unset, rather than sent as an explicit null - which
/// fields are present at all is how LibreNMS decides which SNMP version (or
/// ICMP-only) to add the device as.
/// </summary>
public sealed class AddDeviceRequest
{
    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = string.Empty;

    /// <summary>"v1", "v2c" or "v3" - omit entirely for ICMP-only (see <see cref="SnmpDisabled"/>).</summary>
    [JsonPropertyName("snmpver")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SnmpVersion { get; init; }

    /// <summary>Required for v1/v2c.</summary>
    [JsonPropertyName("community")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Community { get; init; }

    /// <summary>v3: noAuthNoPriv, authNoPriv or authPriv.</summary>
    [JsonPropertyName("authlevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthLevel { get; init; }

    [JsonPropertyName("authname")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthName { get; init; }

    [JsonPropertyName("authpass")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthPass { get; init; }

    /// <summary>v3: MD5 or SHA.</summary>
    [JsonPropertyName("authalgo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthAlgo { get; init; }

    [JsonPropertyName("cryptopass")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CryptoPass { get; init; }

    /// <summary>v3: AES or DES.</summary>
    [JsonPropertyName("cryptoalgo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CryptoAlgo { get; init; }

    /// <summary>ICMP-only: disables SNMP checks and polling for this device entirely.</summary>
    [JsonPropertyName("snmp_disable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SnmpDisabled { get; init; }

    [JsonPropertyName("port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Port { get; init; }

    /// <summary>udp, tcp, udp6 or tcp6.</summary>
    [JsonPropertyName("transport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transport { get; init; }

    /// <summary>Which poller in a distributed-poller setup should own this device. Defaults to 0 (the main poller) when omitted.</summary>
    [JsonPropertyName("poller_group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PollerGroup { get; init; }

    /// <summary>Skips the duplicate-device and SNMP-reachability checks - needed to add a device that cannot answer SNMP right now but should still be added.</summary>
    [JsonPropertyName("force_add")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ForceAdd { get; init; }

    /// <summary>Adds the device as ICMP-only instead of failing outright if the SNMP checks fail.</summary>
    [JsonPropertyName("ping_fallback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PingFallback { get; init; }
}

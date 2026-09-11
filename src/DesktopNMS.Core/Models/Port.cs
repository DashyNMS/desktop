using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>A row from /api/v0/devices/{id}/ports - one network interface on one device.</summary>
public sealed class Port
{
    [JsonPropertyName("port_id")]
    public int PortId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("ifIndex")]
    public int? IfIndex { get; set; }

    [JsonPropertyName("ifName")]
    public string? IfName { get; set; }

    [JsonPropertyName("ifDescr")]
    public string? IfDescr { get; set; }

    /// <summary>The operator-set description, e.g. "uplink to core". Shown as a subtitle under the port's identity.</summary>
    [JsonPropertyName("ifAlias")]
    public string? IfAlias { get; set; }

    [JsonPropertyName("ifType")]
    public string? IfType { get; set; }

    /// <summary>Negotiated/configured link speed in bits per second.</summary>
    [JsonPropertyName("ifSpeed")]
    public long? IfSpeed { get; set; }

    [JsonPropertyName("ifDuplex")]
    public string? IfDuplex { get; set; }

    [JsonPropertyName("ifMtu")]
    public int? IfMtu { get; set; }

    [JsonPropertyName("ifPhysAddress")]
    public string? IfPhysAddress { get; set; }

    /// <summary>"up", "down", "testing", "unknown", ... (RFC 1213 ifOperStatus).</summary>
    [JsonPropertyName("ifOperStatus")]
    public string? IfOperStatus { get; set; }

    /// <summary>Whether the interface is administratively enabled, independent of link state.</summary>
    [JsonPropertyName("ifAdminStatus")]
    public string? IfAdminStatus { get; set; }

    /// <summary>Inbound throughput, bytes per second, as last polled.</summary>
    [JsonPropertyName("ifInOctets_rate")]
    public double? IfInOctetsRate { get; set; }

    /// <summary>Outbound throughput, bytes per second, as last polled.</summary>
    [JsonPropertyName("ifOutOctets_rate")]
    public double? IfOutOctetsRate { get; set; }

    [JsonPropertyName("ifInErrors_delta")]
    public long? IfInErrorsDelta { get; set; }

    [JsonPropertyName("ifOutErrors_delta")]
    public long? IfOutErrorsDelta { get; set; }

    /// <summary>Excluded from monitoring/alerting by an operator, independent of link state.</summary>
    [JsonPropertyName("ignore")]
    public bool Ignore { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>
    /// The port's identity, preferring ifDescr then the SNMP ifName.
    /// <see cref="IfAlias"/> - the operator's own description - is
    /// deliberately not part of this: it belongs alongside as a subtitle, not
    /// as the primary name, the same way LibreNMS's own UI shows it.
    /// </summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(IfDescr) ? IfDescr! :
        !string.IsNullOrWhiteSpace(IfName) ? IfName! :
        $"port {PortId}";

    [JsonIgnore]
    public bool IsUp => string.Equals(IfOperStatus, "up", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => DisplayName;
}

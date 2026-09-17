using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from /api/v0/devices. Present so the alert list can be enriched and so
/// a device view can be added without reworking the client.
/// </summary>
public sealed class Device
{
    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("sysName")]
    public string? SysName { get; set; }

    [JsonPropertyName("display")]
    public string? Display { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("os")]
    public string? Os { get; set; }

    [JsonPropertyName("hardware")]
    public string? Hardware { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>Forces <see cref="Location"/> to win over whatever the device's own sysLocation reports, instead of LibreNMS preferring sysLocation.</summary>
    [JsonPropertyName("override_sysLocation")]
    public bool OverrideSysLocation { get; set; }

    /// <summary>Which poller in a distributed-poller setup owns this device - see <see cref="Api.AddDeviceRequest.PollerGroup"/>.</summary>
    [JsonPropertyName("poller_group")]
    public int PollerGroup { get; set; }

    /// <summary>The raw SNMP system description, e.g. "Onyx,SN2010M,SWv3.10.4408" - a one-line hardware/firmware summary LibreNMS's own device page shows prominently at the top.</summary>
    [JsonPropertyName("sysDescr")]
    public string? SysDescr { get; set; }

    [JsonPropertyName("sysContact")]
    public string? Contact { get; set; }

    /// <summary>Forces <see cref="Contact"/> to win over whatever the device's own sysContact reports, mirroring <see cref="OverrideSysLocation"/>.</summary>
    [JsonPropertyName("override_sysContact")]
    public bool OverrideSysContact { get; set; }

    /// <summary>Excludes this device from fleet-wide up/down availability figures without disabling polling or alerting for it - distinct from <see cref="Ignore"/>.</summary>
    [JsonPropertyName("ignore_status")]
    public bool IgnoreStatus { get; set; }

    /// <summary>The device's SNMP sysObjectID, e.g. ".1.3.6.1.4.1.33049.1.1.1.201015" - identifies the vendor/model MIB, mainly useful for cross-referencing against vendor documentation.</summary>
    [JsonPropertyName("sysObjectID")]
    public string? SysObjectId { get; set; }

    /// <summary>Hardware serial number, when the vendor's MIB exposes one - not every vendor does.</summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    /// <summary>Hostname(s) of whatever this device is recorded as depending on, comma-separated - most devices have none.</summary>
    [JsonPropertyName("dependency_parent_hostname")]
    public string? DependencyParentHostname { get; set; }

    /// <summary>When LibreNMS first added this device.</summary>
    [JsonPropertyName("inserted")]
    public DateTime? Inserted { get; set; }

    /// <summary>When LibreNMS last ran full discovery (not just a poll) against this device.</summary>
    [JsonPropertyName("last_discovered")]
    public DateTime? LastDiscovered { get; set; }

    /// <summary>1 = up, 0 = down.</summary>
    [JsonPropertyName("status")]
    public bool Status { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("ignore")]
    public bool Ignore { get; set; }

    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    [JsonIgnore]
    public string BestName =>
        !string.IsNullOrWhiteSpace(Display) ? Display! :
        !string.IsNullOrWhiteSpace(SysName) ? SysName! :
        !string.IsNullOrWhiteSpace(Hostname) ? Hostname! :
        $"device {DeviceId}";

    /// <summary>
    /// The single state to show for this device. Disabled and ignored both take
    /// priority over the raw up/down reading, since a disabled device's stale
    /// "status" value says nothing about whether it is actually reachable.
    /// </summary>
    [JsonIgnore]
    public DeviceState State =>
        Disabled ? DeviceState.Disabled :
        Ignore ? DeviceState.Ignored :
        Status ? DeviceState.Up :
        DeviceState.Down;

    public override string ToString() => BestName;
}

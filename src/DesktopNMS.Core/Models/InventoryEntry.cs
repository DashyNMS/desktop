using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNMS.Core.Json;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One row of a device's ENTITY-MIB physical inventory (LibreNMS's
/// <c>entPhysical</c> table, from <c>GET inventory/{device}/all</c>) - a
/// chassis, backplane, slot, fan, power supply, module, transceiver or port.
/// The tree comes from <see cref="ContainedIn"/>, which matches the parent's
/// <see cref="Index"/> (not its database id); 0 is a root. See
/// <see cref="Topology.InventoryTree"/>.
/// </summary>
public sealed class InventoryEntry
{
    [JsonPropertyName("entPhysical_id")]
    public int Id { get; set; }

    [JsonPropertyName("entPhysicalIndex")]
    public long Index { get; set; }

    [JsonPropertyName("entPhysicalContainedIn")]
    public long ContainedIn { get; set; }

    /// <summary>Position within its parent - what siblings are ordered by (-1 when unknown).</summary>
    [JsonPropertyName("entPhysicalParentRelPos")]
    public long ParentRelPos { get; set; }

    /// <summary>chassis, backplane, container, powerSupply, fan, sensor, module, port, stack, cpu, other, unknown.</summary>
    [JsonPropertyName("entPhysicalClass")]
    public string? Class { get; set; }

    [JsonPropertyName("entPhysicalName")]
    public string? Name { get; set; }

    [JsonPropertyName("entPhysicalDescr")]
    public string? Description { get; set; }

    [JsonPropertyName("entPhysicalModelName")]
    public string? Model { get; set; }

    [JsonPropertyName("entPhysicalSerialNum")]
    public string? Serial { get; set; }

    [JsonPropertyName("entPhysicalMfgName")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("entPhysicalHardwareRev")]
    public string? HardwareRev { get; set; }

    [JsonPropertyName("entPhysicalFirmwareRev")]
    public string? FirmwareRev { get; set; }

    [JsonPropertyName("entPhysicalSoftwareRev")]
    public string? SoftwareRev { get; set; }

    [JsonPropertyName("entPhysicalAlias")]
    public string? Alias { get; set; }

    [JsonPropertyName("entPhysicalAssetID")]
    public string? AssetId { get; set; }

    /// <summary>"true"/"false" as LibreNMS stores it - see <see cref="IsFieldReplaceable"/>.</summary>
    [JsonPropertyName("entPhysicalIsFRU")]
    [JsonConverter(typeof(LooseStringConverter))]
    public string? IsFru { get; set; }

    [JsonPropertyName("entPhysicalVendorType")]
    public string? VendorType { get; set; }

    [JsonPropertyName("entPhysicalMfgDate")]
    [JsonConverter(typeof(LooseStringConverter))]
    public string? ManufactureDate { get; set; }

    /// <summary>For a port-class entry, the interface it is - links it to the Ports tab.</summary>
    [JsonPropertyName("ifIndex")]
    public int? IfIndex { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>A field-replaceable unit - something that can be swapped on its own (a PSU, a fan tray, an SFP).</summary>
    [JsonIgnore]
    public bool IsFieldReplaceable => string.Equals(IsFru, "true", StringComparison.OrdinalIgnoreCase) || IsFru == "1";

    [JsonIgnore]
    public bool IsPort => string.Equals(Class, "port", StringComparison.OrdinalIgnoreCase);

    /// <summary>The entry's name, or its description, or its index - never blank.</summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
        : !string.IsNullOrWhiteSpace(Description) ? Description.Trim()
        : $"Entity {Index}";
}

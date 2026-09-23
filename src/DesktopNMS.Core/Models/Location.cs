using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from /api/v0/resources/locations - one of LibreNMS's own named,
/// geocoded sites. Distinct from <see cref="Device.Location"/>, a free-text
/// column on the device itself with no foreign key to this table - a device
/// only counts as "at" a location by having this exact name typed into its
/// own Location field.
/// </summary>
public sealed class Location
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("location")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("lat")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseNullableDoubleConverter))]
    public double? Latitude { get; set; }

    [JsonPropertyName("lng")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseNullableDoubleConverter))]
    public double? Longitude { get; set; }

    /// <summary>True (LibreNMS's own default once coordinates are set) keeps <see cref="Latitude"/>/<see cref="Longitude"/> fixed; false lets a device's own reported coordinates overwrite them.</summary>
    [JsonPropertyName("fixed_coordinates")]
    public bool FixedCoordinates { get; set; }
}

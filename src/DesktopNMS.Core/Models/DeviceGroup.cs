using System;
using System.Text.Json.Serialization;
using DesktopNMS.Core.Json;

namespace DesktopNMS.Core.Models;

/// <summary>A row from /api/v0/devicegroups - one of the fleet's own saved device groups (dynamic or static).</summary>
public sealed class DeviceGroup
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string? Description { get; set; }

    /// <summary>"static" or "dynamic" - confirmed present on every row against a live instance.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// A dynamic group's query-builder condition, shown as raw/unparsed JSON
    /// for read-only display only (no editor - see
    /// <see cref="IsEditableAsStatic"/>). LooseStringConverter because this
    /// arrives as a nested JSON object, not a string - confirmed against a
    /// live instance, same shape mismatch as <see cref="Device.Location"/>'s
    /// geocoding object. LibreNMS's older "pattern" field is superseded by
    /// this and comes back empty on a modern instance, so this is what
    /// actually varies per dynamic group.
    /// </summary>
    [JsonPropertyName("rules")]
    [JsonConverter(typeof(LooseStringConverter))]
    public string? Rules { get; set; }

    /// <summary>
    /// True only when this app can positively confirm the group is a plain
    /// static (explicit device-list) group - never inferred from an absent
    /// or unrecognised <see cref="Type"/>. This app can only create/edit
    /// static groups (a dynamic group's rules are a full query-builder blob,
    /// out of scope), so anything ambiguous stays read-only rather than risk
    /// overwriting a dynamic group's rules with a static-shaped update.
    /// </summary>
    [JsonIgnore]
    public bool IsEditableAsStatic => string.Equals(Type, "static", StringComparison.OrdinalIgnoreCase);
}

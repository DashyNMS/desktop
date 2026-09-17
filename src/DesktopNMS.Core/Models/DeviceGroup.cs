using System;
using System.Text.Json.Serialization;

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

    /// <summary>"static" or "dynamic" - confirmed present on every row against a live instance. Dynamic groups also return a "rules" query-builder object, not modelled here since nothing in this app reads it yet.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

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

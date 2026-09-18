using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One node of a rule's jQuery QueryBuilder condition tree (LibreNMS's own
/// <c>builder</c> format) - confirmed live against a real rule. A node is
/// either a group (<see cref="Condition"/>/<see cref="Rules"/> set, "AND" or
/// "OR" over its children) or a leaf condition (<see cref="Field"/>/
/// <see cref="Operator"/>/<see cref="Value"/> set) - the same shape either
/// way, matching how LibreNMS itself serializes it (no separate discriminator
/// field).
/// </summary>
public sealed class AlertConditionNode
{
    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    [JsonPropertyName("rules")]
    public List<AlertConditionNode>? Rules { get; set; }

    /// <summary>Leaf only - same value as <see cref="Field"/>, e.g. "sensors.sensor_class".</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("field")]
    public string? Field { get; set; }

    /// <summary>Leaf only - e.g. "string", "integer".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Leaf only - e.g. "text", "radio".</summary>
    [JsonPropertyName("input")]
    public string? Input { get; set; }

    [JsonPropertyName("operator")]
    public string? Operator { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("valid")]
    public bool Valid { get; set; } = true;

    [JsonIgnore]
    public bool IsGroup => Rules is not null;

    /// <summary>
    /// True only for a single group of leaves with no nested sub-group - the
    /// shape the rule builder UI can edit. A rule whose builder nests a group
    /// inside a group falls back to a read-only view rather than risk
    /// flattening or corrupting a structure this editor doesn't model.
    /// </summary>
    [JsonIgnore]
    public bool IsFlatGroup => IsGroup && Rules!.TrueForAll(r => !r.IsGroup);
}

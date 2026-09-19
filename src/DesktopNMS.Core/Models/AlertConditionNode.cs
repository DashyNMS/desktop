using System.Text.Json.Nodes;
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
/// <remarks>
/// Unset members must be <b>absent</b> from the JSON, not null: LibreNMS's
/// <c>QueryBuilderParser</c> tells groups from leaves with
/// <c>array_key_exists('condition', ...)</c>, and PHP counts a null-valued key
/// as existing - a leaf written as <c>{"condition":null,...}</c> is parsed as
/// an empty group and renders as <c>()</c>, which is exactly what happened to
/// a live rule before the WhenWritingNull attributes below. <see cref="Value"/>
/// is the one exception: jQuery QueryBuilder itself writes <c>"value":null</c>
/// for is_null-style operators.
/// </remarks>
public sealed class AlertConditionNode
{
    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    [JsonPropertyName("rules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AlertConditionNode>? Rules { get; set; }

    /// <summary>Leaf only - same value as <see cref="Field"/>, e.g. "sensors.sensor_class".</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("field")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; set; }

    /// <summary>Leaf only - e.g. "string", "integer".</summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>Leaf only - e.g. "text", "radio".</summary>
    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Input { get; set; }

    [JsonPropertyName("operator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operator { get; set; }

    /// <summary>
    /// A string for single-value operators, a two-element array for
    /// <c>between</c>/<c>not_between</c>, a list for <c>in</c>/<c>not_in</c>,
    /// null for <c>is_null</c>-style operators - kept as raw JSON so every
    /// shape round-trips; see <see cref="ValueList"/> for a uniform view.
    /// </summary>
    [JsonPropertyName("value")]
    public JsonNode? Value { get; set; }

    [JsonPropertyName("valid")]
    public bool Valid { get; set; } = true;

    [JsonIgnore]
    public bool IsGroup => Rules is not null;

    /// <summary>True only for a single group of leaves with no nested sub-group.</summary>
    [JsonIgnore]
    public bool IsFlatGroup => IsGroup && Rules!.TrueForAll(r => !r.IsGroup);

    /// <summary><see cref="Value"/> as strings: one entry for a scalar, one per element for an array, none for null.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ValueList => Value switch
    {
        null => Array.Empty<string>(),
        JsonArray array => array.Select(v => v?.ToString() ?? string.Empty).ToList(),
        _ => new[] { Value.ToString() },
    };

    /// <summary>Builds a scalar <see cref="Value"/>.</summary>
    public static JsonNode? ScalarValue(string? value) => value is null ? null : JsonValue.Create(value);

    /// <summary>Builds an array <see cref="Value"/> (for <c>between</c>/<c>in</c>).</summary>
    public static JsonNode ArrayValue(IEnumerable<string> values) =>
        new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}

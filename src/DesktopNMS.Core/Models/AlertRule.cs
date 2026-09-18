using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>A row from /api/v0/rules.</summary>
public sealed class AlertRule
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("severity")]
    public string? SeverityText { get; set; }

    /// <summary>The legacy condition string, e.g. <c>%devices.status = 0</c>.</summary>
    [JsonPropertyName("rule")]
    public string? Rule { get; set; }

    /// <summary>
    /// jQuery QueryBuilder JSON describing the condition. Stored as a text
    /// column, so it arrives as a JSON string rather than a nested object.
    /// </summary>
    [JsonPropertyName("builder")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseStringConverter))]
    public string? Builder { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("proc")]
    public string? Procedure { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("invert_map")]
    public bool InvertMap { get; set; }

    [JsonPropertyName("devices")]
    public List<int> Devices { get; set; } = new();

    [JsonPropertyName("groups")]
    public List<int> Groups { get; set; } = new();

    [JsonPropertyName("locations")]
    public List<int> Locations { get; set; } = new();

    /// <summary>
    /// Escalation-chain fields this app has no editor for - kept only so a
    /// save via <see cref="AlertRuleWriteRequest"/> can carry them through
    /// unmodified rather than silently wiping them (see that type's own doc
    /// comment).
    /// </summary>
    [JsonPropertyName("alert_operation_id")]
    public int? AlertOperationId { get; set; }

    [JsonPropertyName("operations")]
    public JsonElement? Operations { get; set; }

    [JsonPropertyName("default_operation_step_duration_seconds")]
    public int? DefaultOperationStepDurationSeconds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    [JsonIgnore]
    public AlertSeverity Severity => AlertSeverityExtensions.Parse(SeverityText);

    public override string ToString() => Name ?? $"Rule {Id}";
}

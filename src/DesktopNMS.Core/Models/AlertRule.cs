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

    /// <summary>
    /// The SQL the rule actually runs. Derived from <see cref="Builder"/> by the
    /// server on save unless <see cref="AlertRuleExtra.OverrideQuery"/> is set,
    /// in which case it is the hand-written override entered on the web
    /// editor's Advanced tab (confirmed live on a rule using it).
    /// </summary>
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("extra")]
    public AlertRuleExtra? Extra { get; set; }

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
    /// Escalation-chain (alert operation) fields. Read-only here: there is no
    /// API route to list operations, so nothing can be offered for editing,
    /// and <see cref="AlertRuleWriteRequest"/> deliberately never sends them
    /// (see its doc comment for why omitting is what preserves them).
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

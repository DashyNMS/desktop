using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// POST/PUT body for /api/v0/rules (create/update respectively - see
/// <see cref="Api.IAlertRulesApi"/>), confirmed against the live server's own
/// documented field list. <see cref="AlertOperationId"/>/<see cref="Operations"/>/
/// <see cref="DefaultOperationStepDurationSeconds"/> back escalation chains
/// this app has no UI for - on an update they must be carried through
/// unmodified from the rule being edited (see <see cref="AlertRule"/>'s same
/// fields), never left null, or saving a rule that already has an escalation
/// chain configured would silently wipe it.
/// </summary>
public sealed class AlertRuleWriteRequest
{
    /// <summary>Null on create; the rule being edited on update - LibreNMS's own edit_rule route takes this in the body, not the URL.</summary>
    [JsonPropertyName("rule_id")]
    public int? RuleId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>"ok", "warning", or "critical".</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "critical";

    /// <summary>JSON-serialized <see cref="AlertConditionNode"/> tree.</summary>
    [JsonPropertyName("builder")]
    public string Builder { get; set; } = string.Empty;

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

    [JsonPropertyName("alert_operation_id")]
    public int? AlertOperationId { get; set; }

    [JsonPropertyName("operations")]
    public JsonElement? Operations { get; set; }

    [JsonPropertyName("default_operation_step_duration")]
    public int? DefaultOperationStepDurationSeconds { get; set; }
}

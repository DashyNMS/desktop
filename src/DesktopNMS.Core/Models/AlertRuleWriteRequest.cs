using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// POST/PUT body for /api/v0/rules (create/update respectively - see
/// <see cref="Api.IAlertRulesApi"/>), shaped against LibreNMS's own handler
/// (<c>add_edit_rule</c> in <c>includes/html/api_functions.inc.php</c>) rather
/// than the docs, because the handler's key-presence checks matter:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>extra</c> is rebuilt from scratch on every save, so
/// <see cref="Invert"/>/<see cref="Recovery"/>/<see cref="Acknowledgement"/>
/// are always sent - omitting one resets it, not preserves it.</item>
/// <item><c>alert_operation_id</c>, <c>operations</c> and
/// <c>default_operation_step_duration</c> are the opposite: the handler only
/// touches the rule's escalation chain when those keys are present, and a
/// present-but-null <c>default_operation_step_duration</c> is coerced to
/// <c>0</c> and written. They are deliberately absent from this type so an
/// edit never disturbs an escalation chain this app has no UI for.</item>
/// <item>A create must name at least one device; <c>-1</c> means "global"
/// and is skipped when syncing, so <see cref="Devices"/> is sent as
/// <c>[-1]</c> when empty (the handler otherwise rejects it with 400).</item>
/// <item>The legacy <c>count</c>/<c>delay</c>/<c>interval</c>/<c>mute</c>
/// keys are never sent: their presence makes the handler synthesise a new
/// escalation chain from LibreNMS's default transports.</item>
/// </list>
/// </remarks>
public sealed class AlertRuleWriteRequest
{
    /// <summary>Null on create; the rule being edited on update - LibreNMS's own edit_rule route takes this in the body, not the URL.</summary>
    [JsonPropertyName("rule_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RuleId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>"ok", "warning", or "critical".</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "critical";

    /// <summary>
    /// JSON-serialized <see cref="AlertConditionNode"/> tree. Required by the
    /// handler even when <see cref="OverrideQuery"/> is set (it validates the
    /// builder before it looks at the override).
    /// </summary>
    [JsonPropertyName("builder")]
    public string Builder { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("proc")]
    public string? Procedure { get; set; }

    /// <summary>0 or 1 - the handler compares against the strings '0'/'1'.</summary>
    [JsonPropertyName("disabled")]
    public int Disabled { get; set; }

    /// <summary>"All devices except in list" - run against every device NOT in the targeting lists.</summary>
    [JsonPropertyName("invert_map")]
    public bool InvertMap { get; set; }

    /// <summary>"Invert rule match" - alert when the condition does NOT match. Stored in <c>extra.invert</c>.</summary>
    [JsonPropertyName("invert")]
    public bool Invert { get; set; }

    /// <summary>"Recovery alerts". Stored in <c>extra.recovery</c>.</summary>
    [JsonPropertyName("recovery")]
    public bool Recovery { get; set; } = true;

    /// <summary>"Acknowledgement alerts". Stored in <c>extra.acknowledgement</c>.</summary>
    [JsonPropertyName("acknowledgement")]
    public bool Acknowledgement { get; set; } = true;

    /// <summary>"Override SQL" on the Advanced tab - run <see cref="AdvQuery"/> instead of the builder-derived SQL.</summary>
    [JsonPropertyName("override_query")]
    public bool OverrideQuery { get; set; }

    /// <summary>The hand-written SQL, only meaningful with <see cref="OverrideQuery"/>.</summary>
    [JsonPropertyName("adv_query")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdvQuery { get; set; }

    [JsonPropertyName("devices")]
    public List<int> Devices { get; set; } = new();

    [JsonPropertyName("groups")]
    public List<int> Groups { get; set; } = new();

    [JsonPropertyName("locations")]
    public List<int> Locations { get; set; } = new();
}

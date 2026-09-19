using System.Text.Json.Serialization;
using DesktopNMS.Core.Json;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A rule's <c>extra</c> JSON column, as returned on every rule by
/// /api/v0/rules (confirmed live). Holds the per-rule notification toggles
/// the LibreNMS web editor calls "Invert rule match", "Recovery alerts" and
/// "Acknowledgement alerts", plus the "Override SQL" switch from its
/// Advanced tab. Distinct from <see cref="AlertRule.InvertMap"/>, which is a
/// top-level column ("All devices except in list").
/// </summary>
/// <remarks>
/// LibreNMS's API handler (<c>add_edit_rule</c> in
/// <c>includes/html/api_functions.inc.php</c>) rebuilds this object from
/// scratch on every save rather than merging - so a write that omits these
/// flags resets them, which is why <see cref="AlertRuleWriteRequest"/>
/// always sends all of them explicitly.
/// </remarks>
public sealed class AlertRuleExtra
{
    /// <summary>Alert when the rule's condition does NOT match.</summary>
    [JsonPropertyName("invert")]
    [JsonConverter(typeof(FlexibleBooleanConverter))]
    public bool Invert { get; set; }

    /// <summary>Send a notification when an alert recovers. LibreNMS's runtime treats an absent value as true.</summary>
    [JsonPropertyName("recovery")]
    [JsonConverter(typeof(FlexibleBooleanConverter))]
    public bool Recovery { get; set; } = true;

    /// <summary>Send a notification when an alert is acknowledged. LibreNMS's runtime treats an absent value as true.</summary>
    [JsonPropertyName("acknowledgement")]
    [JsonConverter(typeof(FlexibleBooleanConverter))]
    public bool Acknowledgement { get; set; } = true;

    /// <summary>
    /// Legacy "Mute alerts" flag. Still written by the web editor and still
    /// read by the runtime for recovery-state notifications, but the API's
    /// own save handler has no field for it and drops it on every write -
    /// so this is read-only here, surfaced only so the editor can warn that
    /// saving will clear it.
    /// </summary>
    [JsonPropertyName("mute")]
    [JsonConverter(typeof(FlexibleBooleanConverter))]
    public bool Mute { get; set; }

    [JsonPropertyName("options")]
    public AlertRuleExtraOptions? Options { get; set; }

    [JsonIgnore]
    public bool OverrideQuery => Options?.OverrideQuery ?? false;
}

public sealed class AlertRuleExtraOptions
{
    /// <summary>When true the rule runs <see cref="AlertRule.Query"/> as hand-written SQL instead of the SQL derived from <see cref="AlertRule.Builder"/>.</summary>
    [JsonPropertyName("override_query")]
    [JsonConverter(typeof(FlexibleBooleanConverter))]
    public bool OverrideQuery { get; set; }
}

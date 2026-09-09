using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from the LibreNMS /api/v0/alerts endpoint.
/// </summary>
/// <remarks>
/// The API selects <c>devices.hostname</c>, every column of <c>alerts</c>, and
/// <c>severity</c>/<c>name</c>/<c>proc</c>/<c>notes</c> from <c>alert_rules</c>.
/// Unrecognised columns land in <see cref="AdditionalData"/> so a LibreNMS
/// upgrade that adds columns does not break deserialisation.
/// </remarks>
public sealed class Alert
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("rule_id")]
    public int RuleId { get; set; }

    /// <summary>Raw alerts.state value. Use <see cref="State"/> for the typed form.</summary>
    [JsonPropertyName("state")]
    public int StateValue { get; set; }

    /// <summary>True once LibreNMS has dispatched the alert to its transports.</summary>
    [JsonPropertyName("alerted")]
    public bool Alerted { get; set; }

    [JsonPropertyName("open")]
    public bool Open { get; set; }

    /// <summary>Free-text note; acknowledgements are appended here by the server.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Server-local time the alert last changed state.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }

    /// <summary>
    /// Opaque JSON blob LibreNMS uses for bookkeeping (e.g. until_clear). It
    /// arrives as a string from the database column, but has been seen as an
    /// embedded object, hence the tolerant converter.
    /// </summary>
    [JsonPropertyName("info")]
    [JsonConverter(typeof(DesktopNMS.Core.Json.LooseStringConverter))]
    public string? Info { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    /// <summary>Raw rule severity string. Use <see cref="Severity"/> for the typed form.</summary>
    [JsonPropertyName("severity")]
    public string? SeverityText { get; set; }

    /// <summary>The alert rule name, e.g. "Device Down! Due to no ICMP response.".</summary>
    [JsonPropertyName("name")]
    public string? RuleName { get; set; }

    /// <summary>URL of the rule's procedure/runbook document, if the rule defines one.</summary>
    [JsonPropertyName("proc")]
    public string? Procedure { get; set; }

    /// <summary>Operator notes attached to the rule.</summary>
    [JsonPropertyName("notes")]
    public string? RuleNotes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    [JsonIgnore]
    public AlertState State => AlertStateExtensions.FromValue(StateValue);

    [JsonIgnore]
    public AlertSeverity Severity => AlertSeverityExtensions.Parse(SeverityText);

    [JsonIgnore]
    public bool IsAcknowledged => State == AlertState.Acknowledged;

    [JsonIgnore]
    public string DisplayHostname => string.IsNullOrWhiteSpace(Hostname) ? $"device {DeviceId}" : Hostname!;

    [JsonIgnore]
    public string DisplayRuleName => string.IsNullOrWhiteSpace(RuleName) ? $"Rule {RuleId}" : RuleName!;

    /// <summary>
    /// Identity used to tell "the same alert as last poll" from "a new alert".
    /// The alerts table reuses a row for a rule/device pair, so the id alone is
    /// the right key; the state is tracked separately.
    /// </summary>
    [JsonIgnore]
    public int Key => Id;

    public override string ToString() => $"#{Id} {DisplayHostname}: {DisplayRuleName} [{Severity}/{State}]";
}

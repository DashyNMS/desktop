using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from /api/v0/logs/alertlog. This is where LibreNMS records what
/// actually matched when a rule fired.
/// </summary>
/// <remarks>
/// The <c>details</c> column is gzipped JSON in the database, but the API
/// decompresses and decodes it before returning it, so <see cref="Details"/>
/// arrives as a normal JSON object. Use
/// <see cref="Alerting.AlertFaultParser"/> to turn it into something printable.
/// </remarks>
public sealed class AlertLogEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("rule_id")]
    public int RuleId { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    /// <summary>Raw alert_log.state. Uses the same 0/1/2/3/4 vocabulary as alerts.state.</summary>
    [JsonPropertyName("state")]
    public int StateValue { get; set; }

    [JsonPropertyName("time_logged")]
    public DateTime? TimeLogged { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("sysName")]
    public string? SysName { get; set; }

    /// <summary>
    /// { "contacts": {...}, "rule": [ ...matched rows... ], "diff": {...} }.
    /// Null on older entries, or when the stored blob could not be decoded.
    /// </summary>
    [JsonPropertyName("details")]
    public JsonElement? Details { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    [JsonIgnore]
    public AlertState State => AlertStateExtensions.FromValue(StateValue);

    public override string ToString() => $"alert_log #{Id} rule {RuleId} device {DeviceId} [{State}]";
}

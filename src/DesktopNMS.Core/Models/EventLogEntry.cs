using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A row from /api/v0/logs/eventlog/{device} - LibreNMS's general audit trail
/// for a device (config changes, up/down transitions, polling events, ...),
/// distinct from the alert log: this is everything LibreNMS noted happening,
/// not just what tripped an alert rule.
/// </summary>
public sealed class EventLogEntry
{
    // LibreNMS's eventlog table names its primary key "event_id", not "id"
    // (unlike most other tables) - confirmed against the live API, since
    // trusting "id" here silently deserialized every entry to 0.
    [JsonPropertyName("event_id")]
    public int Id { get; set; }

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("datetime")]
    public DateTime? Timestamp { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Category LibreNMS filed this under, e.g. "system", "sensor", "poller".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Who/what caused it - a username for a manual change, otherwise typically empty (the poller made it).</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>LibreNMS's own severity for the entry. Not yet mapped to a display colour - see EventLogItemViewModel.</summary>
    [JsonPropertyName("severity")]
    public int? Severity { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public override string ToString() => Message ?? $"event {Id}";
}

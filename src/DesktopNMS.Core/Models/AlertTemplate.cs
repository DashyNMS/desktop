using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>A row from /api/v0/alert_templates.</summary>
public sealed class AlertTemplate
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The template body itself (Blade-style, per LibreNMS's own templating).</summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>The title shown when an alert this template is attached to recovers.</summary>
    [JsonPropertyName("title_rec")]
    public string? TitleRec { get; set; }

    /// <summary>Ids of the rules this template is attached to.</summary>
    [JsonPropertyName("alert_rules")]
    public List<int> AlertRules { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public override string ToString() => Name ?? $"Template {Id}";
}

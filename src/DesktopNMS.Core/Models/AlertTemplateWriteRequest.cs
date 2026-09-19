using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>POST body for /api/v0/alert_templates - see <see cref="Api.IAlertTemplatesApi"/>.</summary>
public sealed class AlertTemplateWriteRequest
{
    /// <summary>Null on create; the template being edited on update - LibreNMS's own edit_alert_template route reuses the create route, addressed by this field.</summary>
    [JsonPropertyName("template_id")]
    public int? TemplateId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("template")]
    public string Template { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("title_rec")]
    public string? TitleRec { get; set; }

    [JsonPropertyName("alert_rules")]
    public List<int> AlertRules { get; set; } = new();
}

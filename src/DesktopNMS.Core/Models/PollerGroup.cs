using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>A distributed-poller group, as used by <see cref="Api.AddDeviceRequest.PollerGroup"/>.</summary>
public sealed class PollerGroup
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("group_name")]
    public string GroupName { get; set; } = string.Empty;

    [JsonPropertyName("descr")]
    public string? Descr { get; set; }

    /// <summary>
    /// This app's custom ComboBox ControlTemplate falls back to this for the
    /// closed selection box rather than actually honouring DisplayMemberPath/
    /// SelectionBoxItemTemplate - without this override, every instance
    /// rendered as the same default "Namespace.PollerGroup" text regardless
    /// of which one was selected, since none of them differ on that.
    /// </summary>
    public override string ToString() => GroupName;
}

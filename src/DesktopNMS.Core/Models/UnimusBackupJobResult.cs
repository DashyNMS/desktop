using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>The result of triggering an on-demand backup job - <c>PATCH jobs/backup?id=</c>'s <c>data</c> object.</summary>
public sealed class UnimusBackupJobResult
{
    [JsonPropertyName("accepted")]
    public int Accepted { get; set; }

    [JsonPropertyName("refused")]
    public int Refused { get; set; }

    [JsonPropertyName("sentForDiscovery")]
    public int SentForDiscovery { get; set; }

    [JsonPropertyName("unManaged")]
    public int UnManaged { get; set; }

    /// <summary>True when Unimus actually queued the job for this device rather than refusing it (unknown device, unmanaged, etc.).</summary>
    [JsonIgnore]
    public bool WasAccepted => Accepted > 0;
}

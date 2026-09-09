using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Updates;

/// <summary>
/// The fields DashyNMS uses from a GitHub "release" object
/// (GET /repos/{owner}/{repo}/releases/latest).
/// </summary>
public sealed class GitHubRelease
{
    /// <summary>The tag the release was cut from, e.g. "v0.2.0".</summary>
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    /// <summary>The release's display title, e.g. "0.2.0 - Health tab".</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The release notes, in Markdown.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>The GitHub web page for this release.</summary>
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}

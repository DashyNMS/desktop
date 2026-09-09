namespace DesktopNMS.Core.Updates;

/// <summary>Reads release information from a GitHub repository.</summary>
public interface IGitHubReleaseService
{
    /// <summary>
    /// The latest published, non-draft, non-prerelease release, or null if the
    /// repository has none or the request failed. Never throws.
    /// </summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);
}

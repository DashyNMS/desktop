namespace DesktopNMS.Core.Updates;

/// <summary>Reads release information from a GitHub repository.</summary>
public interface IGitHubReleaseService
{
    /// <summary>
    /// The latest published, non-draft, non-prerelease release, or null if the
    /// repository has none or the request failed. Never throws.
    /// </summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent published releases, newest first, including
    /// pre-releases (never drafts - GitHub excludes them for an unauthenticated
    /// caller anyway). Used to find the newest preview build once a user has
    /// opted into seeing them. Returns an empty list rather than throwing if
    /// the request fails.
    /// </summary>
    Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(int count = 10, CancellationToken cancellationToken = default);
}

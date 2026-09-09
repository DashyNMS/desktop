using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Updates;

/// <summary>
/// Reads the latest release of a fixed GitHub repository via the public,
/// unauthenticated REST API. Read-only: this never downloads or runs
/// anything, it only reports what the repository's Releases page says.
/// </summary>
public sealed class GitHubReleaseService : IGitHubReleaseService, IDisposable
{
    // GitHub's unauthenticated rate limit is 60 requests/hour per IP, which is
    // vastly more than an occasional per-launch check needs, so no token is
    // used here - this only ever reads a public repository.
    private const string Owner = "DashyNMS";
    private const string Repo = "desktop";

    private readonly HttpClient _http;
    private readonly ILogger<GitHubReleaseService> _logger;

    public GitHubReleaseService(ILogger<GitHubReleaseService> logger)
    {
        _logger = logger;

        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(10),
        };

        // The GitHub API rejects requests with no User-Agent.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DashyNMS", "1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"repos/{Owner}/{Repo}/releases/latest";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 just means the repo has no releases yet - not worth logging as a warning.
                _logger.LogDebug("GitHub releases check returned HTTP {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, DNS down, GitHub unreachable, malformed response, etc:
            // an update check failing must never be treated as an app error.
            _logger.LogDebug(ex, "Could not check GitHub for the latest release");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

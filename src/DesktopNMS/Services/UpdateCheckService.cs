using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DesktopNMS.Services;

/// <summary>
/// Checks GitHub for a newer DashyNMS release than the one currently running,
/// for the Settings "About" page and an occasional startup toast.
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>The version of the running build, e.g. "0.1.0".</summary>
    string CurrentVersion { get; }

    /// <summary>
    /// Fetches the latest release and compares it to <see cref="CurrentVersion"/>.
    /// Never throws. When <paramref name="notifyIfNewer"/> is true and a newer
    /// release is found that has not already been notified about (tracked in
    /// settings), shows a toast once.
    /// </summary>
    /// <param name="notifyIfNewer">Show a toast for a not-yet-notified newer release.</param>
    /// <param name="includePreviewBuildsOverride">
    /// When set, overrides <c>AppSettings.IncludePreviewBuilds</c> for this one
    /// check instead of reading the saved setting - used by the Settings
    /// dialog so toggling the checkbox and checking again reflects
    /// immediately, without needing Save first.
    /// </param>
    Task<UpdateCheckResult> CheckAsync(bool notifyIfNewer, bool? includePreviewBuildsOverride = null, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a single update check.</summary>
public sealed record UpdateCheckResult(bool Succeeded, GitHubRelease? LatestRelease, bool IsNewerVersionAvailable);

public sealed class UpdateCheckService : IUpdateCheckService
{
    private readonly IGitHubReleaseService _releases;
    private readonly ISettingsStore _settings;
    private readonly ITrayNotifier _trayFallback;
    private readonly ILogger<UpdateCheckService> _logger;

    private bool _toastsUnavailable;

    public UpdateCheckService(
        IGitHubReleaseService releases,
        ISettingsStore settings,
        ITrayNotifier trayFallback,
        ILogger<UpdateCheckService> logger)
    {
        _releases = releases;
        _settings = settings;
        _trayFallback = trayFallback;
        _logger = logger;
    }

    public string CurrentVersion { get; } = GetCurrentVersion();

    /// <summary>
    /// The assembly's own <see cref="AssemblyName.Version"/> can only ever be
    /// a plain numeric core - it silently drops any "-preview.N" suffix, so
    /// every preview build of a given release reported the exact same
    /// version here regardless of which preview it actually was (issue
    /// #107). The full version the build was published with (preserved via
    /// <see cref="AssemblyInformationalVersionAttribute"/> at compile time -
    /// confirmed present in the compiled assembly's own metadata) is read
    /// back here from the native Win32 version resource on the running
    /// executable instead of via <see cref="Assembly.GetCustomAttribute"/> -
    /// this app publishes as a single-file self-contained executable, and
    /// that attribute could not reliably be read back off
    /// <see cref="Assembly.GetEntryAssembly"/> at runtime once bundled that
    /// way (confirmed live: correct in the compiled DLL's IL metadata,
    /// empty once running as the bundled single-file exe). The Win32
    /// resource - which every DashyNMS.exe carries regardless of how it was
    /// published - does not have this problem. The SDK also appends a
    /// "+&lt;git-sha&gt;" build-metadata suffix to the version, which is
    /// stripped here - meaningless for display or comparison.
    /// </summary>
    private static string GetCurrentVersion()
    {
        var informational = TryGetProductVersion();

        if (string.IsNullOrWhiteSpace(informational))
        {
            informational = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
        }

        if (string.IsNullOrWhiteSpace(informational))
        {
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var metadataIndex = informational.IndexOf('+');
        return metadataIndex >= 0 ? informational[..metadataIndex] : informational;
    }

    private static string? TryGetProductVersion()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(processPath).ProductVersion;
        }
        catch (Exception)
        {
            // Falls through to the AssemblyInformationalVersion/AssemblyName
            // fallbacks below - reading Win32 resource data is not something
            // that should ever be able to take the app down.
            return null;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(bool notifyIfNewer, bool? includePreviewBuildsOverride = null, CancellationToken cancellationToken = default)
    {
        var includePreviewBuilds = includePreviewBuildsOverride ?? _settings.Current.IncludePreviewBuilds;

        var release = includePreviewBuilds
            ? await GetBestIncludingPreviewsAsync(cancellationToken).ConfigureAwait(false)
            : await _releases.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);

        if (release is null || release.Draft || string.IsNullOrWhiteSpace(release.TagName))
        {
            return new UpdateCheckResult(Succeeded: release is not null, LatestRelease: null, IsNewerVersionAvailable: false);
        }

        var isNewer = ReleaseVersion.IsNewer(release.TagName, CurrentVersion);

        if (isNewer && notifyIfNewer && release.TagName != _settings.Current.LastNotifiedUpdateVersion)
        {
            ShowUpdateToast(release);
            _settings.Current.LastNotifiedUpdateVersion = release.TagName;
            _settings.Save();
        }

        return new UpdateCheckResult(Succeeded: true, LatestRelease: release, IsNewerVersionAvailable: isNewer);
    }

    /// <summary>
    /// GitHub's own "latest" endpoint never returns a pre-release, so once
    /// preview builds are opted into this instead lists recent releases and
    /// picks the best-ranked one (see <see cref="ReleaseVersion.Compare"/>) -
    /// a published stable release still wins over an older preview tag.
    /// </summary>
    private async Task<GitHubRelease?> GetBestIncludingPreviewsAsync(CancellationToken cancellationToken)
    {
        var releases = await _releases.GetReleasesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        GitHubRelease? best = null;
        foreach (var candidate in releases)
        {
            if (candidate.Draft || string.IsNullOrWhiteSpace(candidate.TagName))
            {
                continue;
            }

            if (best is null || ReleaseVersion.Compare(candidate.TagName, best.TagName) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private void ShowUpdateToast(GitHubRelease release)
    {
        var title = release.Prerelease
            ? $"DashyNMS {release.TagName} (preview) is available"
            : $"DashyNMS {release.TagName} is available";
        var body = string.IsNullOrWhiteSpace(release.Name) ? "A new version is ready to download." : release.Name!;

        try
        {
            if (_toastsUnavailable)
            {
                _trayFallback.ShowBalloon(title, body, AlertSeverity.Ok);
                return;
            }

            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .AddAttributionText("DashyNMS");

            if (Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var url))
            {
                builder.AddButton(new ToastButton()
                    .SetContent("View on GitHub")
                    .SetProtocolActivation(url));
            }

            builder.AddButton(new ToastButtonDismiss("Dismiss"));

            builder.Show(toast =>
            {
                toast.Tag = "update-available";
                toast.Group = "DashyNMS";
                toast.ExpirationTime = DateTimeOffset.Now.AddDays(3);
            });

            _logger.LogInformation("Shown an update-available toast for {Version}", release.TagName);
        }
        catch (Exception ex)
        {
            _toastsUnavailable = true;
            _logger.LogWarning(ex, "Could not show the update-available toast");
            _trayFallback.ShowBalloon(title, body, AlertSeverity.Ok);
        }
    }
}

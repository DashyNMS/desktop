using System;
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
    Task<UpdateCheckResult> CheckAsync(bool notifyIfNewer, CancellationToken cancellationToken = default);
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

    public string CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<UpdateCheckResult> CheckAsync(bool notifyIfNewer, CancellationToken cancellationToken = default)
    {
        var release = await _releases.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);

        if (release is null || release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
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

    private void ShowUpdateToast(GitHubRelease release)
    {
        var title = $"DashyNMS {release.TagName} is available";
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

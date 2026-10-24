using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Updates;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Checks GitHub for a newer DashyNMS release than the one currently running,
/// for the Settings "About" page and an occasional startup toast.
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>The version of the running build, e.g. "0.1.0".</summary>
    string CurrentVersion { get; }

    /// <summary>Package-manager installs only announce updates; they do not download installers.</summary>
    bool DownloadsAutomatically => true;
    bool CanInstallUpdate => DownloadsAutomatically;
    bool IsDownloading => DownloadsAutomatically && ReadyUpdate is null;

    /// <summary>The action appropriate to the running package.</summary>
    string UpdateActionText => "Restart to update";

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

    /// <summary>A newer version, downloaded and verified, waiting to be installed - null until there is one.</summary>
    ReadyUpdate? ReadyUpdate { get; }

    /// <summary>Raised (on a background thread) when <see cref="ReadyUpdate"/> changes.</summary>
    event EventHandler? ReadyUpdateChanged;

    /// <summary>
    /// The installer has started - the app should exit now so it can be
    /// replaced; the installer reopens it on the new version. Raised on a
    /// background thread.
    /// </summary>
    event EventHandler? InstallStarted;

    /// <summary>
    /// Installs the ready update - fetching it first if this process
    /// hasn't (a toast clicked after a restart, say). False if there's no
    /// update to install, or it couldn't be started.
    /// </summary>
    Task<bool> InstallAsync();

    /// <summary>At start-up: says so if DashyNMS was just updated, and clears out installers it no longer needs.</summary>
    void OnStartup();
}

/// <summary>The outcome of a single update check.</summary>
public sealed record UpdateCheckResult(bool Succeeded, GitHubRelease? LatestRelease, bool IsNewerVersionAvailable);

/// <summary>A downloaded, verified update: its version ("v1.2.0"), installer, and release page.</summary>
public sealed record ReadyUpdate(string Version, string InstallerPath, string? ReleaseUrl);

/// <summary>Where a version's release notes live.</summary>
public static class UpdateReleases
{
    public static string ReleasePageUrl(string version) =>
        "https://github.com/DashyNMS/desktop/releases/tag/" + (version.StartsWith('v') ? version : "v" + version);
}

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

public sealed class UpdateCheckService : IUpdateCheckService
{
    private readonly IGitHubReleaseService _releases;
    private readonly ISettingsStore _settings;
    private readonly ITrayNotifier _trayFallback;
    private readonly ILogger<UpdateCheckService> _logger;

    private readonly object _gate = new();

    // Its own client: a 70 MB installer needs far longer than the API's 10 seconds.
    private readonly HttpClient _download = CreateDownloadClient();

    private bool _toastsUnavailable;
    private ReadyUpdate? _ready;
    private Task? _preparing;
    private string? _preparingVersion;

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

    public ReadyUpdate? ReadyUpdate
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    public event EventHandler? ReadyUpdateChanged;

    public event EventHandler? InstallStarted;

    private static HttpClient CreateDownloadClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("DashyNMS", "1"));
        return http;
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

        // A newer release is fetched in the background, and the toast (if
        // wanted) says so once it's ready to install - see PrepareAsync.
        if (isNewer)
        {
            var notify = notifyIfNewer && release.TagName != _settings.Current.LastNotifiedUpdateVersion;
            _ = StartPreparing(release, notify);
        }

        return new UpdateCheckResult(Succeeded: true, LatestRelease: release, IsNewerVersionAvailable: isNewer);
    }

    private Task StartPreparing(GitHubRelease release, bool notify)
    {
        lock (_gate)
        {
            // Already fetched: just say so, if that's wanted and not yet done.
            if (_ready?.Version == release.TagName)
            {
                if (notify)
                {
                    ShowReadyToast(release);
                    MarkNotified(release);
                }

                return Task.CompletedTask;
            }

            // Fetching it now.
            if (_preparingVersion == release.TagName && _preparing is { IsCompleted: false })
            {
                return _preparing;
            }

            _preparingVersion = release.TagName;
            _preparing = Task.Run(() => PrepareAsync(release, notify));
            return _preparing;
        }
    }

    /// <summary>
    /// Downloads and verifies the release's installer (see UpdatePackage),
    /// then offers "Restart to update". If it can't - no installer this app
    /// would run, or the download failed - the toast points at the release
    /// page instead, as before.
    /// </summary>
    private async Task PrepareAsync(GitHubRelease release, bool notify)
    {
        try
        {
            if (UpdatePackage.FindInstaller(release) is not { } asset)
            {
                _logger.LogInformation("{Version} has no installer this app can verify - offering the release page", release.TagName);
                NotifyAvailable(release, notify);
                return;
            }

            var path = await UpdatePackage.DownloadAsync(_download, asset, AppPaths.UpdatesDirectory).ConfigureAwait(false);
            UpdatePackage.DeleteOthers(AppPaths.UpdatesDirectory, Path.GetFileName(path));

            lock (_gate)
            {
                _ready = new ReadyUpdate(release.TagName!, path, release.HtmlUrl);
            }

            _logger.LogInformation("{Version} is downloaded and verified, ready to install", release.TagName);
            ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);

            if (notify)
            {
                ShowReadyToast(release);
                MarkNotified(release);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not download {Version} - offering the release page", release.TagName);
            NotifyAvailable(release, notify);
        }
    }

    private void NotifyAvailable(GitHubRelease release, bool notify)
    {
        if (notify)
        {
            ShowUpdateToast(release);
            MarkNotified(release);
        }
    }

    // Quietly: nothing else reacts to it, and this runs off the UI thread.
    private void MarkNotified(GitHubRelease release)
    {
        _settings.Current.LastNotifiedUpdateVersion = release.TagName;
        _settings.SaveQuietly();
    }

    public async Task<bool> InstallAsync()
    {
        // Not fetched in this process yet (a toast clicked after a restart):
        // check again - a verified installer already on disk is reused.
        if (ReadyUpdate is null)
        {
            await CheckAsync(notifyIfNewer: false).ConfigureAwait(false);
            Task? preparing;
            lock (_gate)
            {
                preparing = _preparing;
            }

            if (preparing is not null)
            {
                await preparing.ConfigureAwait(false);
            }
        }

        if (ReadyUpdate is not { } ready || !File.Exists(ready.InstallerPath))
        {
            return false;
        }

        try
        {
            // The installer runs silently, closing this app if it's still
            // running; once it's finished - whether or not it succeeded - the
            // app is started again from where it's installed. Done here
            // rather than by the installer so it works for any version's
            // installer, including ones from before this existed.
            var app = Path.Combine(InstallDirectory(), "DashyNMS.exe");
            var command = $"start \"\" /wait \"{ready.InstallerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS & start \"\" \"{app}\"";

            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"{command}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ready.InstallerPath),
            });

            _logger.LogInformation("Installing {Version} - DashyNMS will exit and reopen on it", ready.Version);
            InstallStarted?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start the installer for {Version}", ready.Version);
            return false;
        }
    }

    // installer/DashyNMS.iss's AppId - Inno Setup records where it installed under it.
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B3D2C7A-4E1F-4A6B-8C5D-2F7A9E1B3C64}_is1";

    /// <summary>
    /// Where the installer puts DashyNMS: wherever it was installed before
    /// (the installer reuses that), else its default. Not simply this
    /// process's folder - a development build isn't where it installs.
    /// </summary>
    private static string InstallDirectory()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
            {
                return location;
            }
        }
        catch (Exception)
        {
            // Falls back to the default below.
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DashyNMS");
    }

    public void OnStartup()
    {
        var last = _settings.Current.LastRunVersion;
        if (last != CurrentVersion)
        {
            // Not on a first run - there's no "before" to have been updated from.
            if (last is not null && ReleaseVersion.IsNewer(CurrentVersion, last))
            {
                ShowUpdatedToast();
            }

            _settings.Current.LastRunVersion = CurrentVersion;
            _settings.SaveQuietly();
        }

        // Installers for this version or older have done their job.
        try
        {
            var directory = AppPaths.UpdatesDirectory;
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var name = Path.GetFileName(file);
                    var version = name.StartsWith("DashyNMS-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? name["DashyNMS-Setup-".Length..^".exe".Length]
                        : null;

                    if (version is null || !ReleaseVersion.IsNewer(version, CurrentVersion))
                    {
                        File.Delete(file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not tidy the updates folder");
        }
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

        ShowToast(title, body, "update-available", builder =>
        {
            if (Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var url))
            {
                builder.AddButton(new ToastButton()
                    .SetContent("View on GitHub")
                    .SetProtocolActivation(url));
            }
        });
    }

    private void ShowReadyToast(GitHubRelease release)
    {
        var title = release.Prerelease
            ? $"DashyNMS {release.TagName} (preview) is ready to install"
            : $"DashyNMS {release.TagName} is ready to install";

        ShowToast(title, "Restart DashyNMS to update - it reopens on the new version.", "update-available", builder =>
        {
            // Handled by AlertNotificationService, like Acknowledge.
            builder.AddButton(new ToastButton()
                .SetContent("Restart to update")
                .AddArgument("action", "install-update"));

            if (Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var url))
            {
                builder.AddButton(new ToastButton()
                    .SetContent("What's new")
                    .SetProtocolActivation(url));
            }
        });
    }

    private void ShowUpdatedToast()
    {
        ShowToast($"DashyNMS updated to {CurrentVersion}", "The update installed and DashyNMS reopened on it.", "updated", builder =>
        {
            if (Uri.TryCreate(ReleasePageUrl(CurrentVersion), UriKind.Absolute, out var url))
            {
                builder.AddButton(new ToastButton()
                    .SetContent("What's new")
                    .SetProtocolActivation(url));
            }
        });
    }

    /// <summary>The GitHub release page for a version ("1.2.0" or "v1.2.0").</summary>
    public static string ReleasePageUrl(string version) =>
        "https://github.com/DashyNMS/desktop/releases/tag/" + (version.StartsWith('v') ? version : "v" + version);

    private void ShowToast(string title, string body, string tag, Action<ToastContentBuilder> addButtons)
    {
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

            addButtons(builder);
            builder.AddButton(new ToastButtonDismiss("Dismiss"));

            builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = "DashyNMS";
                toast.ExpirationTime = DateTimeOffset.Now.AddDays(3);
            });

            _logger.LogInformation("Shown the \"{Title}\" toast", title);
        }
        catch (Exception ex)
        {
            _toastsUnavailable = true;
            _logger.LogWarning(ex, "Could not show the \"{Title}\" toast", title);
            _trayFallback.ShowBalloon(title, body, AlertSeverity.Ok);
        }
    }
}

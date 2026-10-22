using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Updates;
using DesktopNMS.Demo;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>The running package decides whether it can apply an update.</summary>
public interface IUpdateInstallation
{
    string? Runtime { get; }
    string ActionText { get; }
    bool RestartsApp { get; }
    Task<bool> InstallAsync(string verifiedPackage);
}

public interface IUpdateAnnouncements
{
    Task ShowAsync(string title, string body, bool ready);
}

/// <summary>One release policy for all desktop platforms; package installation stays in the app.</summary>
public sealed class DesktopUpdateService : IUpdateCheckService, IDisposable
{
    private readonly IGitHubReleaseService _releases;
    private readonly ISettingsStore _settings;
    private readonly IUpdateInstallation _installation;
    private readonly IUpdateAnnouncements _announcements;
    private readonly DemoMode _demo;
    private readonly HttpClient _http;
    private readonly ILogger<DesktopUpdateService> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private Task _preparing = Task.CompletedTask;
    private string? _wantedVersion;
    private string? _preparingVersion;
    private bool _downloading;
    private bool _preparingPreview;
    private bool _readyPreview;
    private int _checkGeneration;
    private ReadyUpdate? _ready;
    private GitHubReleaseAsset? _readyAsset;
    private readonly string _directory;

    public DesktopUpdateService(IGitHubReleaseService releases, ISettingsStore settings,
        IUpdateInstallation installation, IUpdateAnnouncements announcements, DemoMode demo,
        ILogger<DesktopUpdateService> logger, HttpClient? http = null, string? directory = null,
        string? currentVersion = null)
    {
        _releases = releases; _settings = settings; _installation = installation;
        _announcements = announcements; _demo = demo; _logger = logger;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DashyNMS/2");
        _directory = directory ?? AppPaths.UpdatesDirectory;
        CurrentVersion = currentVersion ?? RunningVersion();
    }

    public static string RunningVersion()
    {
        string? version = null;
        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } path)
        {
            try { version = FileVersionInfo.GetVersionInfo(path).ProductVersion; } catch { }
        }
        if (string.IsNullOrWhiteSpace(version))
            version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (version ?? "0.0.0").Split('+')[0];
    }

    public string CurrentVersion { get; }
    public bool DownloadsAutomatically => _installation.Runtime is not null;
    public bool IsDownloading { get { lock (_gate) return _downloading; } }
    public string UpdateActionText => _installation.ActionText;
    public ReadyUpdate? ReadyUpdate { get { lock (_gate) return _ready; } }
    public event EventHandler? ReadyUpdateChanged;
    public event EventHandler? InstallStarted;
    internal Task WhenIdle { get { lock (_gate) return _preparing; } }

    public async Task<UpdateCheckResult> CheckAsync(bool notifyIfNewer, bool? includePreviewBuildsOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (_demo.IsActive) return new(true, null, false);
        var generation = Interlocked.Increment(ref _checkGeneration);
        try
        {
            var previews = includePreviewBuildsOverride ?? _settings.Current.IncludePreviewBuilds;
            lock (_gate)
            {
                if (!previews && (_readyPreview || _preparingPreview))
                {
                    _wantedVersion = null; _ready = null; _readyAsset = null; _downloading = false;
                    ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            GitHubRelease? release;
            if (previews)
            {
                var releases = await _releases.GetReleasesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                release = releases.Where(r => !r.Draft && !string.IsNullOrWhiteSpace(r.TagName))
                    .OrderByDescending(r => r.TagName, Comparer<string?>.Create(ReleaseVersion.Compare)).FirstOrDefault();
            }
            else release = await _releases.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release is null) return new(false, null, false);
            if (release.Draft || (!previews && release.Prerelease) || string.IsNullOrWhiteSpace(release.TagName))
                return new(true, null, false);
            var newer = ReleaseVersion.IsNewer(release.TagName, CurrentVersion);
            lock (_gate)
            {
                if (generation != Volatile.Read(ref _checkGeneration)) return new(true, release, newer);
                var version = newer ? release.TagName : null;
                if (_wantedVersion != version)
                {
                    _wantedVersion = version; _ready = null; _readyAsset = null;
                    ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
                }
                if (newer && (_preparing.IsCompleted || _preparingVersion != release.TagName))
                {
                    _preparingVersion = release.TagName;
                    _preparingPreview = release.Prerelease;
                    _preparing = PrepareAsync(release, notifyIfNewer);
                }
            }
            return new(true, release, newer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check for a desktop update");
            return new(false, null, false);
        }
    }

    private async Task PrepareAsync(GitHubRelease release, bool notify)
    {
        // Yield before taking the lock: callers can always observe the pending task.
        await Task.Yield();
        var ready = false;
        try
        {
            var asset = _installation.Runtime is { } runtime ? UpdatePackage.FindForRuntime(release, runtime) : null;
            if (asset is not null)
            {
                lock (_gate) { if (_wantedVersion != release.TagName) return; _downloading = true; }
                ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
                var path = await UpdatePackage.DownloadAsync(_http, asset, _directory).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_wantedVersion != release.TagName) return;
                    _readyAsset = asset;
                    _readyPreview = release.Prerelease;
                    _ready = new(release.TagName!, path, UpdateReleases.ReleasePageUrl(release.TagName!));
                    ready = true;
                }
                ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not prepare desktop update {Version}", release.TagName); }
        finally
        {
            lock (_gate) { if (_wantedVersion == release.TagName) _downloading = false; }
            ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
        }
        lock (_gate) { if (_wantedVersion != release.TagName) return; }
        if (!notify || _settings.Current.LastNotifiedUpdateVersion == release.TagName) return;
        try
        {
            await _announcements.ShowAsync($"DashyNMS {release.TagName} is {(ready ? "ready to install" : "available")}",
                ready ? _installation.ActionText : "Open Settings, About to view the release and installation instructions.", ready).ConfigureAwait(false);
            _settings.Current.LastNotifiedUpdateVersion = release.TagName;
            _settings.SaveQuietly();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not announce a desktop update"); }
    }

    public async Task<bool> InstallAsync()
    {
        if (_demo.IsActive || !await _installGate.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            if (ReadyUpdate is null) { await CheckAsync(false).ConfigureAwait(false); await WhenIdle.ConfigureAwait(false); }
            ReadyUpdate? ready; GitHubReleaseAsset? asset;
            lock (_gate) { ready = _ready; asset = _readyAsset; }
            if (ready is null || asset is null) return false;
            // Recheck a cached file immediately before handing it to the OS.
            var path = await UpdatePackage.DownloadAsync(_http, asset, _directory).ConfigureAwait(false);
            lock (_gate) { if (_wantedVersion != ready.Version || _ready != ready) return false; }
            if (!await _installation.InstallAsync(path).ConfigureAwait(false)) return false;
            if (_installation.RestartsApp) InstallStarted?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not install a desktop update"); return false; }
        finally { _installGate.Release(); }
    }

    public void OnStartup()
    {
        if (_demo.IsActive) return;
        if (WhatsNewNotes.MarkSeenIfFreshInstall(_settings.Current, CurrentVersion)) _settings.SaveQuietly();
        if (_settings.Current.LastRunVersion == CurrentVersion) return;
        _settings.Current.LastRunVersion = CurrentVersion;
        _settings.SaveQuietly();
    }

    public void Dispose() => _http.Dispose();
}

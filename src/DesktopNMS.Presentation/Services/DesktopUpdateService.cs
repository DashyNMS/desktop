using DesktopNMS.Diagnostics;
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
    string? UnavailableReason => null;
    Task PrepareAsync(string verifiedPackage, string version, CancellationToken cancellationToken) => Task.CompletedTask;
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
    private readonly SemaphoreSlim _packageGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task _preparing = Task.CompletedTask;
    private string? _wantedVersion;
    private Preparation? _operation;
    private GitHubRelease? _latestRelease;
    private bool _automaticDownloads;
    private bool _readyPreview;
    private int _checkGeneration;
    private ReadyUpdate? _ready;
    private GitHubReleaseAsset? _readyAsset;
    private readonly string _directory;
    private readonly UpdateMetrics _metrics;
    private MetricMode DeliveryMode => !CanInstallUpdate ? MetricMode.Notification : DownloadsAutomatically ? MetricMode.Automatic : MetricMode.Manual;

    public DesktopUpdateService(IGitHubReleaseService releases, ISettingsStore settings,
        IUpdateInstallation installation, IUpdateAnnouncements announcements, DemoMode demo,
        ILogger<DesktopUpdateService> logger, HttpClient? http = null, string? directory = null,
        string? currentVersion = null, IAppMetrics? metrics = null)
    {
        _releases = releases; _settings = settings; _installation = installation;
        _announcements = announcements; _demo = demo; _logger = logger;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DashyNMS/2");
        _directory = directory ?? AppPaths.UpdatesDirectory;
        CurrentVersion = currentVersion ?? RunningVersion();
        _metrics = new UpdateMetrics(_demo.IsActive ? null : metrics, _directory, CurrentVersion);
        _automaticDownloads = _settings.Current.DownloadUpdatesAutomatically;
        _settings.Changed += OnSettingsChanged;
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

    private string? _installationError;
    public string? InstallationError
    {
        get => _installation.UnavailableReason ?? _installationError;
        private set => _installationError = value;
    }
    public string CurrentVersion { get; }
    public bool CanInstallUpdate => _installation.Runtime is not null;
    public bool DownloadsAutomatically => CanInstallUpdate && _settings.Current.DownloadUpdatesAutomatically;
    public bool IsDownloading { get { lock (_gate) return _operation?.Downloading == true; } }
    public string UpdateActionText => _installation.ActionText;
    public ReadyUpdate? ReadyUpdate { get { lock (_gate) return _ready; } }
    public event EventHandler? ReadyUpdateChanged;
    public event EventHandler? InstallStarted;
    internal Task WhenIdle { get { lock (_gate) return _preparing; } }

    public async Task<UpdateCheckResult> CheckAsync(bool notifyIfNewer, bool? includePreviewBuildsOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (_demo.IsActive || _lifetime.IsCancellationRequested) return new(true, null, false);
        var generation = Interlocked.Increment(ref _checkGeneration);
        var previews = includePreviewBuildsOverride ?? _settings.Current.IncludePreviewBuilds;
        var timing = _metrics.Begin(MetricArea.UpdateCheck, null, previews, DeliveryMode);
        var outcome = MetricOutcome.Failed;
        string? target = null;
        try
        {
            lock (_gate)
            {
                if (!previews && (_readyPreview || _latestRelease?.Prerelease == true || _operation?.Release.Prerelease == true))
                {
                    _operation?.Cancellation.Cancel();
                    _wantedVersion = null; _latestRelease = null; _ready = null; _readyAsset = null;
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
            target = release.TagName;
            outcome = MetricOutcome.Success;
            if (release.Draft || (!previews && release.Prerelease) || string.IsNullOrWhiteSpace(release.TagName))
                return new(true, null, false);
            var newer = ReleaseVersion.IsNewer(release.TagName, CurrentVersion);
            if (timing.IsCurrent) _metrics.Begin(MetricArea.UpdateAvailability, release.TagName, previews, DeliveryMode)
                .Complete(newer ? MetricOutcome.Available : MetricOutcome.Current);
            lock (_gate)
            {
                if (_lifetime.IsCancellationRequested || generation != Volatile.Read(ref _checkGeneration)) return new(true, release, newer);
                var version = newer ? release.TagName : null;
                if (_wantedVersion != version)
                {
                    _operation?.Cancellation.Cancel();
                    _wantedVersion = version; _ready = null; _readyAsset = null; InstallationError = null;
                    ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
                }
                _latestRelease = newer ? release : null;
                if (newer) StartPreparation(release, notifyIfNewer, manual: false);
            }
            return new(true, release, newer);
        }
        catch (Exception ex)
        {
            outcome = ex is OperationCanceledException ? MetricOutcome.Cancelled : MetricOutcome.Failed;
            _logger.LogWarning(ex, "Could not check for a desktop update");
            return new(false, null, false);
        }
        finally { timing.Complete(outcome, target); }
    }

    private void StartPreparation(GitHubRelease release, bool notify, bool manual)
    {
        if (_lifetime.IsCancellationRequested || _operation is { } pending
            && !pending.Cancellation.IsCancellationRequested && pending.Release.TagName == release.TagName
            && (!manual || pending.Manual)) return;
        _operation?.Cancellation.Cancel();
        var operation = new Preparation(release, manual, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        _operation = operation;
        _preparing = PrepareAsync(operation, notify);
    }

    private async Task PrepareAsync(Preparation operation, bool notify)
    {
        await Task.Yield();
        try { await PrepareAndAnnounceAsync(operation, notify).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (_operation == operation) _operation = null;
                operation.Cancellation.Dispose();
            }
            ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task PrepareAndAnnounceAsync(Preparation operation, bool notify)
    {
        var release = operation.Release;
        var token = operation.Cancellation.Token;
        bool ready;
        lock (_gate) ready = _ready?.Version == release.TagName;
        try
        {
            var asset = (operation.Manual || DownloadsAutomatically) && _installation.Runtime is { } runtime
                ? UpdatePackage.FindForRuntime(release, runtime) : null;
            if (asset is not null)
            {
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_wantedVersion != release.TagName) return;
                    operation.Downloading = true;
                }
                ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
                var download = _metrics.Begin(MetricArea.UpdateDownload, release.TagName, release.Prerelease,
                    operation.Manual ? MetricMode.Manual : MetricMode.Automatic);
                string path;
                try
                {
                    InstallationError = null;
                    path = await DownloadAsync(asset, token).ConfigureAwait(false);
                    await _installation.PrepareAsync(path, release.TagName!, token).ConfigureAwait(false);
                    download.Complete(MetricOutcome.Success);
                }
                catch (OperationCanceledException) { download.Complete(MetricOutcome.Cancelled); throw; }
                catch { download.Complete(MetricOutcome.Failed); throw; }
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_wantedVersion != release.TagName) return;
                    _readyAsset = asset;
                    _readyPreview = release.Prerelease;
                    _ready = new(release.TagName!, path, UpdateReleases.ReleasePageUrl(release.TagName!));
                    ready = true;
                }
                ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_operation == operation && _wantedVersion == release.TagName && !token.IsCancellationRequested)
                    InstallationError = ex is IOException ? ex.Message : null;
            }
            _logger.LogWarning(ex, "Could not prepare desktop update {Version}", release.TagName);
        }
        finally
        {
            lock (_gate)
            {
                operation.Downloading = false;
            }
            ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
        }
        lock (_gate) { if (token.IsCancellationRequested || _wantedVersion != release.TagName) return; }
        if (!notify || _settings.Current.LastNotifiedUpdateVersion == release.TagName) return;
        try
        {
            await _announcements.ShowAsync($"DashyNMS {release.TagName} is {(ready ? "ready to install" : "available")}",
                ready ? _installation.ActionText : "Open Settings, Updates to view the release and installation instructions.", ready).ConfigureAwait(false);
            _settings.Current.LastNotifiedUpdateVersion = release.TagName;
            _settings.SaveQuietly();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not announce a desktop update"); }
    }

    public async Task<bool> InstallAsync()
    {
        if (_demo.IsActive || _lifetime.IsCancellationRequested || !CanInstallUpdate || !await _installGate.WaitAsync(0).ConfigureAwait(false)) return false;
        UpdateTiming? install = null;
        var launched = false;
        try
        {
            bool needsCheck;
            lock (_gate) needsCheck = _ready is null && _latestRelease is null;
            if (needsCheck) await CheckAsync(false).ConfigureAwait(false);
            Task preparing;
            lock (_gate)
            {
                if (_ready is null && _latestRelease is { } latest) StartPreparation(latest, notify: false, manual: true);
                preparing = _ready is null ? _preparing : Task.CompletedTask;
            }
            await preparing.ConfigureAwait(false);
            ReadyUpdate? ready; GitHubReleaseAsset? asset; bool readyPreview; string? target;
            lock (_gate)
            {
                ready = _ready; asset = _readyAsset;
                target = ready?.Version ?? _latestRelease?.TagName;
                readyPreview = ready is not null ? _readyPreview : _latestRelease?.Prerelease == true;
            }
            install = _metrics.Begin(MetricArea.UpdateInstall, target, readyPreview, MetricMode.Manual);
            if (ready is null || asset is null) { install.Complete(MetricOutcome.Unavailable); return false; }
            InstallationError = null;
            // Recheck a cached file immediately before handing it to the OS.
            var path = await DownloadAsync(asset, _lifetime.Token).ConfigureAwait(false);
            lock (_gate) { if (_lifetime.IsCancellationRequested || _wantedVersion != ready.Version || _ready != ready) return false; }
            _metrics.MarkPending(install);
            if (!await _installation.InstallAsync(path).ConfigureAwait(false)) return false;
            launched = true;
            install.Complete(MetricOutcome.Launched);
            if (_installation.RestartsApp) InstallStarted?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            InstallationError = ex is IOException ? ex.Message : null;
            _logger.LogWarning(ex, "Could not install a desktop update"); return false;
        }
        finally
        {
            if (!launched && install is not null) { _metrics.Withdraw(); install.Complete(_lifetime.IsCancellationRequested ? MetricOutcome.Cancelled : MetricOutcome.Failed); }
            _installGate.Release();
        }
    }

    public void OnStartup()
    {
        if (_demo.IsActive) return;
        if (!_metrics.ConfirmStartup(CurrentVersion))
            _metrics.ConfirmUntrackedStartup(_settings.Current.LastRunVersion, CurrentVersion);
        if (WhatsNewNotes.MarkSeenIfFreshInstall(_settings.Current, CurrentVersion)) _settings.SaveQuietly();
        if (_settings.Current.LastRunVersion == CurrentVersion) return;
        _settings.Current.LastRunVersion = CurrentVersion;
        _settings.SaveQuietly();
    }

    private async Task<string> DownloadAsync(GitHubReleaseAsset asset, CancellationToken token)
    {
        await _packageGate.WaitAsync(token).ConfigureAwait(false);
        try { return await UpdatePackage.DownloadAsync(_http, asset, _directory, token).ConfigureAwait(false); }
        finally { _packageGate.Release(); }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!DiagnosticsConsent.Metrics(settings)) _metrics.Withdraw();
        lock (_gate)
        {
            if (_automaticDownloads == settings.DownloadUpdatesAutomatically || _lifetime.IsCancellationRequested) return;
            _automaticDownloads = settings.DownloadUpdatesAutomatically;
            if (!_automaticDownloads && _operation is { Manual: false } automatic) automatic.Cancellation.Cancel();
            if (_automaticDownloads && _ready is null && _latestRelease is { } latest) StartPreparation(latest, notify: false, manual: false);
        }
        ReadyUpdateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        lock (_gate) { if (_lifetime.IsCancellationRequested) return; _lifetime.Cancel(); }
        _http.Dispose();
    }

    private sealed class Preparation(GitHubRelease release, bool manual, CancellationTokenSource cancellation)
    {
        public GitHubRelease Release { get; } = release;
        public bool Manual { get; } = manual;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool Downloading { get; set; }
    }
}

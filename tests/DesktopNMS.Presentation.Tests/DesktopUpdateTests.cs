using System.Net;
using System.Security.Cryptography;
using System.Text;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Updates;
using DesktopNMS.Demo;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class DesktopUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dashy-update-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Package = Encoding.UTF8.GetBytes("example package bytes");
    private readonly Releases _releases = new();
    private readonly Installation _installation = new();
    private readonly Announcements _announcements = new();
    private readonly Download _download = new();
    private readonly ISettingsStore _settings;
    public DesktopUpdateTests() => _settings = new SettingsStore(NullLogger<SettingsStore>.Instance, Path.Combine(_directory, "settings.json"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private DesktopUpdateService Create(bool demo = false) => new(_releases, _settings, _installation, _announcements,
        new DemoMode(demo), NullLogger<DesktopUpdateService>.Instance, new HttpClient(_download), Path.Combine(_directory, "updates"), "2.0.0");

    [Fact]
    public async Task A_verified_windows_update_announces_once_and_installs()
    {
        using var service = Create(); var exits = 0; service.InstallStarted += (_, _) => exits++;
        Assert.True((await service.CheckAsync(true)).IsNewerVersionAvailable); await service.WhenIdle;
        Assert.NotNull(service.ReadyUpdate); Assert.False(service.IsDownloading);
        await service.CheckAsync(true); await service.WhenIdle;
        Assert.Equal(1, _announcements.Calls); Assert.True(_announcements.Ready);
        Assert.True(await service.InstallAsync()); Assert.Equal(1, exits); Assert.Equal(1, _installation.Calls);
    }

    [Fact]
    public async Task A_package_manager_install_announces_without_downloading_or_installing()
    {
        _installation.Runtime = null;
        using var service = Create();
        Assert.True((await service.CheckAsync(true)).IsNewerVersionAvailable); await service.WhenIdle;
        Assert.Null(service.ReadyUpdate); Assert.Equal(0, _download.Calls); Assert.False(_announcements.Ready);
        Assert.False(await service.InstallAsync()); Assert.Equal(0, _installation.Calls);
    }

    [Fact]
    public async Task Opening_a_mac_disk_image_does_not_shutdown_the_app()
    {
        _installation.Runtime = "osx-arm64"; _installation.RestartsApp = false; _installation.ActionText = "Open update disk image";
        _releases.Latest = Release("2.1.0", "osx-arm64");
        using var service = Create(); var exits = 0; service.InstallStarted += (_, _) => exits++;
        await service.CheckAsync(false); await service.WhenIdle;
        Assert.EndsWith("osx-arm64.dmg", service.ReadyUpdate!.InstallerPath);
        Assert.Equal("Open update disk image", service.UpdateActionText);
        Assert.True(await service.InstallAsync()); Assert.Equal(0, exits);
    }

    [Fact]
    public async Task A_tampered_cached_package_cannot_reach_the_installer()
    {
        using var service = Create();
        await service.CheckAsync(false); await service.WhenIdle;
        await File.WriteAllTextAsync(service.ReadyUpdate!.InstallerPath, "tampered");
        _download.Read = _ => Task.FromResult(new byte[Package.Length]);
        Assert.False(await service.InstallAsync()); Assert.Equal(0, _installation.Calls);
    }

    [Fact]
    public async Task A_failed_download_finishes_without_a_ready_update()
    {
        _download.Read = _ => Task.FromResult(new byte[Package.Length]);
        using var service = Create();
        await service.CheckAsync(true); await service.WhenIdle;
        Assert.False(service.IsDownloading); Assert.Null(service.ReadyUpdate); Assert.False(_announcements.Ready);
    }

    [Fact]
    public async Task An_older_in_flight_download_cannot_replace_a_newer_ready_update()
    {
        var held = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _download.Read = request => request.RequestUri!.AbsolutePath.Contains("2.1.0") ? Hold() : Task.FromResult(Package);
        Task<byte[]> Hold() { started.TrySetResult(); return held.Task; }
        using var service = Create();
        await service.CheckAsync(false); var old = service.WhenIdle; await started.Task;
        _releases.Latest = Release("2.2.0");
        await service.CheckAsync(false); await service.WhenIdle;
        held.SetResult(Package); await old;
        Assert.Equal("v2.2.0", service.ReadyUpdate!.Version);
    }

    [Fact]
    public async Task Opting_out_of_previews_clears_a_preview_even_when_the_stable_check_fails()
    {
        var preview = Release("2.2.0-preview.1"); preview.Prerelease = true;
        _releases.All = new[] { Release("2.1.0"), preview };
        using var service = Create();
        await service.CheckAsync(false, true); await service.WhenIdle;
        Assert.Equal(preview.TagName, service.ReadyUpdate!.Version);
        _releases.Latest = null;
        Assert.False((await service.CheckAsync(false, false)).Succeeded);
        Assert.Null(service.ReadyUpdate);
    }

    [Fact]
    public async Task The_demo_does_not_query_releases_download_or_install()
    {
        using var service = Create(demo: true);
        Assert.False((await service.CheckAsync(true)).IsNewerVersionAvailable);
        Assert.False(await service.InstallAsync()); Assert.Equal(0, _releases.Calls); Assert.Equal(0, _download.Calls);
    }

    private static GitHubRelease Release(string version, string runtime = "win-x64")
    {
        var name = runtime == "win-x64" ? $"DashyNMS-Setup-{version}.exe" : $"DashyNMS-{version}-{runtime}.dmg";
        return new() { TagName = "v" + version, Assets = { new() { Name = name, Size = Package.Length,
            Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Package)),
            BrowserDownloadUrl = UpdatePackage.DownloadPrefix + "v" + version + "/" + name } } };
    }
    private sealed class Releases : IGitHubReleaseService
    {
        public GitHubRelease? Latest = Release("2.1.0"); public IReadOnlyList<GitHubRelease> All = Array.Empty<GitHubRelease>(); public int Calls;
        public Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(Latest); }
        public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(int count = 10, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(All); }
    }
    private sealed class Installation : IUpdateInstallation
    {
        public string? Runtime { get; set; } = "win-x64"; public string ActionText { get; set; } = "Restart to update"; public bool RestartsApp { get; set; } = true;
        public int Calls; public Task<bool> InstallAsync(string package) { Assert.True(File.Exists(package)); Calls++; return Task.FromResult(true); }
    }
    private sealed class Announcements : IUpdateAnnouncements
    {
        public int Calls; public bool Ready;
        public Task ShowAsync(string title, string body, bool ready) { Calls++; Ready = ready; return Task.CompletedTask; }
    }
    private sealed class Download : HttpMessageHandler
    {
        public int Calls; public Func<HttpRequestMessage, Task<byte[]>> Read = _ => Task.FromResult(Package);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(await Read(request)) }; }
    }
}

public sealed class AppImageReplacementTests
{
    [LinuxFact]
    public void A_failed_restart_restores_the_previous_executable()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dashy-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var image = Path.Combine(folder, "DashyNMS.AppImage"); var package = Path.Combine(folder, "new.AppImage");
            File.WriteAllText(image, "old"); File.WriteAllText(package, "new");
            Assert.Throws<IOException>(() => AppImageReplacement.Apply(package, image, _ => false));
            Assert.Equal("old", File.ReadAllText(image));
            Assert.True(AppImageReplacement.Apply(package, image, path => File.ReadAllText(path) == "new"));
            Assert.Equal("new", File.ReadAllText(image));
            if (OperatingSystem.IsLinux()) Assert.True(File.GetUnixFileMode(image).HasFlag(UnixFileMode.UserExecute));
            Assert.Equal(2, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, true); }
    }
    private sealed class LinuxFactAttribute : FactAttribute
    { public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "AppImage replacement requires Linux."; } }
}

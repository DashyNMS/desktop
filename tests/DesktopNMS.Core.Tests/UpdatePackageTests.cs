using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopNMS.Core.Updates;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class UpdatePackageTests : IDisposable
{
    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("pretend this is DashyNMS-Setup-1.2.0.exe");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dashynms-update-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static GitHubReleaseAsset Asset(string? digest = null, long? size = null, string name = "DashyNMS-Setup-1.2.0.exe", string? url = null) => new()
    {
        Name = name,
        Size = size ?? Installer.Length,
        Digest = digest ?? "sha256:" + Sha256(Installer),
        BrowserDownloadUrl = url ?? UpdatePackage.DownloadPrefix + "v1.2.0/" + name,
    };

    [Fact]
    public void A_release_parses_its_installer_asset_from_GitHub()
    {
        const string json = """
            {"tag_name":"v1.2.0","prerelease":false,"draft":false,"html_url":"https://github.com/DashyNMS/desktop/releases/tag/v1.2.0",
             "assets":[{"name":"DashyNMS-Setup-1.2.0.exe","size":69966634,
               "digest":"sha256:f83418f355db0dbdba39c0fe05084c210bbbe37471fe90a38deaf7aea19bc371",
               "browser_download_url":"https://github.com/DashyNMS/desktop/releases/download/v1.2.0/DashyNMS-Setup-1.2.0.exe"}]}
            """;

        var release = JsonSerializer.Deserialize<GitHubRelease>(json)!;
        var asset = UpdatePackage.FindInstaller(release);

        Assert.NotNull(asset);
        Assert.Equal("f83418f355db0dbdba39c0fe05084c210bbbe37471fe90a38deaf7aea19bc371", UpdatePackage.ExpectedSha256(asset!));
    }

    [Theory]
    [InlineData("notes.txt", null)]                                                     // not the installer
    [InlineData("DashyNMS-Setup-1.2.0.exe", "https://example.net/DashyNMS-Setup-1.2.0.exe")] // not from the repo's own releases
    public void Only_the_repos_own_installer_is_picked(string name, string? url)
    {
        var release = new GitHubRelease { Assets = { Asset(name: name, url: url) } };

        Assert.Null(UpdatePackage.FindInstaller(release));
    }

    [Fact]
    public void An_installer_without_a_SHA_256_is_never_picked()
    {
        var release = new GitHubRelease { Assets = { Asset(digest: "md5:abc") } };

        Assert.Null(UpdatePackage.FindInstaller(release));
    }

    [Fact]
    public async Task A_matching_download_is_kept()
    {
        using var http = new HttpClient(new FakeHandler(Installer));

        var path = await UpdatePackage.DownloadAsync(http, Asset(), _directory);

        Assert.Equal(Installer, await File.ReadAllBytesAsync(path));
        Assert.Equal("DashyNMS-Setup-1.2.0.exe", Path.GetFileName(path));
    }

    [Fact]
    public async Task A_download_that_doesnt_match_its_SHA_256_is_thrown_away()
    {
        using var http = new HttpClient(new FakeHandler(Encoding.ASCII.GetBytes("tampered!! this is not the installer.....")));

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackage.DownloadAsync(http, Asset(), _directory));

        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task A_verified_copy_already_there_isnt_downloaded_again()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "DashyNMS-Setup-1.2.0.exe"), Installer);
        var handler = new FakeHandler(Installer);
        using var http = new HttpClient(handler);

        await UpdatePackage.DownloadAsync(http, Asset(), _directory);

        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public void Old_installers_are_tidied_away()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "DashyNMS-Setup-1.1.0.exe"), "old");
        File.WriteAllText(Path.Combine(_directory, "DashyNMS-Setup-1.2.0.exe.partial"), "half");
        File.WriteAllText(Path.Combine(_directory, "DashyNMS-Setup-1.2.0.exe"), "new");

        UpdatePackage.DeleteOthers(_directory, "DashyNMS-Setup-1.2.0.exe");

        Assert.Equal(new[] { "DashyNMS-Setup-1.2.0.exe" }, Directory.EnumerateFiles(_directory).Select(Path.GetFileName));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly byte[] _body;

        public FakeHandler(byte[] body) => _body = body;

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
        }
    }
}

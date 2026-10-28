using System.Security.Cryptography;
using System.Text.Json;
using DesktopNMS.Services;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class ComponentVersionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dashynms-versions-" + Guid.NewGuid().ToString("N"));
    private string Executable => Path.Combine(_directory, "dashynms-mcp");
    public ComponentVersionsTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private async Task Metadata(string version = "1.2.3-preview.4+abcdef", string? hash = null)
    {
        await File.WriteAllTextAsync(Executable, "test executable; never launched");
        var sha = hash ?? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Executable)));
        await File.WriteAllTextAsync(Path.Combine(_directory, ComponentVersions.MetadataFile), JsonSerializer.Serialize(
            new ComponentVersions.McpBuild(1, version, new string('a', 40), "linux-arm64", sha)));
    }

    [Fact]
    public async Task Full_preview_identity_is_verified_without_starting_an_executable()
    {
        await Metadata();
        var component = await ComponentVersions.ReadMcpAsync(Executable);
        Assert.Equal("1.2.3-preview.4+abcdef", component.Version);
        Assert.Equal("Bundled", component.State);
        Assert.Contains(new string('a', 40), component.Details);
        Assert.DoesNotContain(_directory, ComponentVersions.Format(new[] { component }));
    }

    [Fact]
    public async Task Replacing_executable_does_not_reuse_old_verified_identity()
    {
        await Metadata();
        await File.WriteAllTextAsync(Executable, "replacement");
        var component = await ComponentVersions.ReadMcpAsync(Executable);
        Assert.Equal(ComponentVersions.Unavailable, component.Version);
        Assert.Contains("does not match", component.State);
    }

    [Fact]
    public async Task Legacy_revision_is_explicitly_unverified_and_missing_executable_is_missing()
    {
        await File.WriteAllTextAsync(Executable, "legacy");
        await File.WriteAllTextAsync(Path.Combine(_directory, "BUILD.txt"), $"DashyNMS MCP {new string('b', 40)} (win-x64)\n");
        var component = await ComponentVersions.ReadMcpAsync(Executable);
        Assert.Equal(ComponentVersions.Unavailable, component.Version);
        Assert.Contains("Unverified bundle provenance", component.Details);
        File.Delete(Executable);
        Assert.Equal("Missing", (await ComponentVersions.ReadMcpAsync(Executable)).State);
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":42}")]
    public async Task Corrupt_or_unknown_metadata_is_unavailable(string json)
    {
        await File.WriteAllTextAsync(Executable, "test");
        await File.WriteAllTextAsync(Path.Combine(_directory, ComponentVersions.MetadataFile), json);
        var component = await ComponentVersions.ReadMcpAsync(Executable);
        Assert.Equal(ComponentVersions.Unavailable, component.Version);
        Assert.Equal("Bundle metadata unavailable", component.State);
    }

    [Fact]
    public async Task Metadata_cannot_inject_paths_addresses_or_multiline_text_into_support_details()
    {
        await Metadata("https://private.example/token\nsecret");
        var text = ComponentVersions.Format(new[] { await ComponentVersions.ReadMcpAsync(Executable) });
        Assert.DoesNotContain("private.example", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain(_directory, text);
    }

    [Fact]
    public void Bug_report_keeps_template_and_other_parameters_with_one_encoded_body()
    {
        var uri = new Uri("https://github.com/DashyNMS/desktop/issues/new?body=" + Uri.EscapeDataString("Describe it\nWindows: example\nSettings › About › Open log folder") + "&labels=bug");
        var result = ComponentVersions.AddToBugReport(uri, new[] { new ComponentVersion("Core", "2.0.0-ci.42", "Loaded") });
        var parts = result.Query.TrimStart('?').Split('&');
        Assert.Contains("labels=bug", parts);
        var body = Uri.UnescapeDataString(Assert.Single(parts, p => p.StartsWith("body="))[5..]);
        Assert.Contains("Describe it\nOS: example", body);
        Assert.Contains("Core: 2.0.0-ci.42 (Loaded)", body);
        Assert.Contains("Architecture:", body);
        Assert.Contains("Settings › Diagnostics › Open log folder", body);
    }

    [Fact]
    public void Mac_bundle_metadata_is_in_resources_while_other_layouts_use_executable_directory()
    {
        var executable = Path.Combine(_directory, "DashyNMS.app", "Contents", "MacOS", "mcp", "dashynms-mcp");
        Assert.Equal(Path.Combine(_directory, "DashyNMS.app", "Contents", "Resources", "mcp"),
            ComponentVersions.MetadataDirectory(executable, macOS: true));
        Assert.Equal(Path.GetDirectoryName(executable), ComponentVersions.MetadataDirectory(executable, macOS: false));
        Assert.Equal(_directory, ComponentVersions.MetadataDirectory(Executable, macOS: true));
    }

    [Fact]
    public async Task Sensor_state_uses_applied_options_and_does_not_need_a_download()
    {
        using var store = new DesktopNMS.Core.Sensors.SensorDefinitionStore(
            DesktopNMS.Core.Sensors.SensorDefinitionPaths.ForInstallation(_directory));
        Assert.Equal("Disabled", ComponentVersions.Sensor(store).State);
        await store.ConfigureAsync(new DesktopNMS.Core.Sensors.SensorDefinitionOptions { Enabled = true });
        Assert.Equal("Not downloaded", ComponentVersions.Sensor(store).State);
        Assert.Equal(ComponentVersions.Unavailable, ComponentVersions.Sensor(store).Version);
    }

    [Fact]
    public async Task Failed_catalogue_download_shows_safe_state_without_copying_exception_details()
    {
        using var http = new HttpClient(new FailedDownload());
        using var store = new DesktopNMS.Core.Sensors.SensorDefinitionStore(
            DesktopNMS.Core.Sensors.SensorDefinitionPaths.ForInstallation(_directory), http);
        await store.ConfigureAsync(new DesktopNMS.Core.Sensors.SensorDefinitionOptions { Enabled = true });
        Assert.False(await store.SynchroniseAsync(force: true));
        var component = ComponentVersions.Sensor(store);
        Assert.Equal("Unavailable", component.State);
        Assert.DoesNotContain("private.example", ComponentVersions.Format(new[] { component }));
    }

    private sealed class FailedDownload : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("https://private.example/token");
    }

    [Fact]
    public void Compact_version_keeps_preview_label_and_preserves_full_build_identity_for_support()
    {
        var component = new ComponentVersion("Core", "2.0.0-preview.4+abcdef", "Loaded");
        Assert.Equal("2.0.0-preview.4", component.DisplayVersion);
        Assert.True(component.HasDetails);
        Assert.Contains("2.0.0-preview.4+abcdef", component.BuildDetails);
        Assert.Contains("2.0.0-preview.4+abcdef", ComponentVersions.Format(new[] { component }));
    }

    [Fact]
    public void Missing_sensor_store_has_explicit_state() =>
        Assert.Equal("Unavailable in this build", ComponentVersions.Sensor(null).State);
}

using DesktopNMS.Core.Sensors;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class DesktopSensorDefinitionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dashynms-definition-default-" + Guid.NewGuid().ToString("N"));
    private SensorDefinitionPaths Paths => SensorDefinitionPaths.ForInstallation(_directory);
    public DesktopSensorDefinitionsTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task New_installation_enables_official_definitions_and_persists_across_launches()
    {
        using (var first = new SensorDefinitionStore(Paths))
        {
            await DesktopSensorDefinitions.InitialiseAsync(first, NullLogger.Instance);
            Assert.True(first.Options.Enabled);
            Assert.False(first.Options.UseCustomRepository);
            Assert.True(File.Exists(Paths.SettingsFile));
        }
        using var next = new SensorDefinitionStore(Paths);
        await DesktopSensorDefinitions.InitialiseAsync(next, NullLogger.Instance);
        Assert.True(next.Options.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_preference_and_source_survive_startup(bool enabled)
    {
        var options = new SensorDefinitionOptions { Enabled = enabled, UseCustomRepository = true,
            CustomRepository = "https://github.com/example/definitions" };
        using (var first = new SensorDefinitionStore(Paths)) await first.ConfigureAsync(options);
        var before = await File.ReadAllBytesAsync(Paths.SettingsFile);
        using var next = new SensorDefinitionStore(Paths);
        await DesktopSensorDefinitions.InitialiseAsync(next, NullLogger.Instance);
        Assert.Equal(options, next.Options);
        Assert.Equal(before, await File.ReadAllBytesAsync(Paths.SettingsFile));
    }

    [Fact]
    public async Task Damaged_preference_is_not_overwritten_by_a_default()
    {
        await File.WriteAllTextAsync(Paths.SettingsFile, "{damaged");
        using var store = new SensorDefinitionStore(Paths);
        await DesktopSensorDefinitions.InitialiseAsync(store, NullLogger.Instance);
        Assert.Equal("{damaged", await File.ReadAllTextAsync(Paths.SettingsFile));
        Assert.False(store.Options.Enabled);
    }

    [Fact]
    public async Task Demo_does_not_create_a_preference_or_enable_downloads()
    {
        using var store = new SensorDefinitionStore(Paths);
        await DesktopSensorDefinitions.InitialiseAsync(store, NullLogger.Instance, enableByDefault: false);
        Assert.False(store.Options.Enabled);
        Assert.False(File.Exists(Paths.SettingsFile));
    }

    [Fact]
    public async Task Unwritable_preference_does_not_prevent_startup()
    {
        Directory.CreateDirectory(Paths.SettingsFile);
        using var store = new SensorDefinitionStore(Paths);
        await DesktopSensorDefinitions.InitialiseAsync(store, NullLogger.Instance);
        Assert.True(Directory.Exists(Paths.SettingsFile));
    }
}

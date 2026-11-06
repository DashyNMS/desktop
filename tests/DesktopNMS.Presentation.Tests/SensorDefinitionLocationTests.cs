using DesktopNMS.Core.Sensors;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class SensorDefinitionLocationTests : IDisposable
{
    private const string CustomPreference = """{"enabled":false,"useCustomRepository":true,"customRepository":"https://github.com/example/definitions"}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dashynms-definition-location-" + Guid.NewGuid().ToString("N"));
    private string DataDirectory => Path.Combine(_root, "data");
    private string Applications => Path.Combine(_root, "Applications");
    private SensorDefinitionPaths Current => SensorDefinitionLocation.InDataDirectory(DataDirectory);
    private SensorDefinitionPaths Legacy => SensorDefinitionPaths.ForInstallation(
        Path.Combine(Applications, "DashyNMS.app", "Contents", "MacOS"), macOS: true);

    public SensorDefinitionLocationTests()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Path.Combine(Applications, "DashyNMS.app", "Contents", "MacOS"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Preference_and_definitions_live_directly_in_the_data_folder()
    {
        var paths = Current;
        Assert.Equal(Path.Combine(DataDirectory, "sensor-definitions.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(DataDirectory, "sensor-definitions"), paths.DefinitionsDirectory);
        Assert.Equal(DataDirectory, Path.GetDirectoryName(paths.SettingsFile));
        Assert.Equal(DataDirectory, Path.GetDirectoryName(paths.DefinitionsDirectory));
    }

    [Fact]
    public async Task Store_accepts_the_data_folder_and_a_new_installation_writes_nothing_beside_the_app()
    {
        using (var store = new SensorDefinitionStore(Current))
        {
            await DesktopSensorDefinitions.InitialiseAsync(store, NullLogger.Instance);
            Assert.True(store.Options.Enabled);
        }

        Assert.True(File.Exists(Current.SettingsFile));
        Assert.False(File.Exists(Legacy.SettingsFile));
        Assert.Equal(new[] { Path.Combine(Applications, "DashyNMS.app") }, Directory.EnumerateFileSystemEntries(Applications));
    }

    [Fact]
    public async Task Legacy_preference_moves_across_and_the_old_files_go()
    {
        await File.WriteAllTextAsync(Legacy.SettingsFile, CustomPreference);
        Directory.CreateDirectory(Path.Combine(Legacy.DefinitionsDirectory, "sources"));
        await File.WriteAllTextAsync(Path.Combine(Legacy.DefinitionsDirectory, "sources", "index.json"), "{}");

        Assert.True(SensorDefinitionLocation.MigrateLegacy(Current, () => Legacy));

        Assert.Equal(CustomPreference, await File.ReadAllTextAsync(Current.SettingsFile));
        Assert.False(File.Exists(Legacy.SettingsFile));
        Assert.False(Directory.Exists(Legacy.DefinitionsDirectory));

        using var store = new SensorDefinitionStore(Current);
        await DesktopSensorDefinitions.InitialiseAsync(store, NullLogger.Instance);
        Assert.False(store.Options.Enabled);
        Assert.True(store.Options.UseCustomRepository);
        Assert.Equal(CustomPreference, await File.ReadAllTextAsync(Current.SettingsFile));
    }

    [Fact]
    public async Task Existing_preference_in_the_data_folder_wins_and_the_old_one_is_removed()
    {
        await File.WriteAllTextAsync(Current.SettingsFile, """{"enabled":true}""");
        await File.WriteAllTextAsync(Legacy.SettingsFile, CustomPreference);

        Assert.False(SensorDefinitionLocation.MigrateLegacy(Current, () => Legacy));

        Assert.Equal("""{"enabled":true}""", await File.ReadAllTextAsync(Current.SettingsFile));
        Assert.False(File.Exists(Legacy.SettingsFile));
    }

    [Fact]
    public void Nothing_to_move_leaves_the_data_folder_alone()
    {
        Assert.False(SensorDefinitionLocation.MigrateLegacy(Current, () => Legacy));
        Assert.False(File.Exists(Current.SettingsFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(DataDirectory));
    }

    [Fact]
    public void A_failure_working_out_the_old_location_is_ignored()
    {
        Assert.False(SensorDefinitionLocation.MigrateLegacy(Current,
            () => SensorDefinitionPaths.ForInstallation(_root, appImage: "relative/DashyNMS.AppImage")));
        Assert.False(SensorDefinitionLocation.MigrateLegacy(Current, () => throw new InvalidOperationException()));
        Assert.False(File.Exists(Current.SettingsFile));
    }

    [Fact]
    public async Task Oversized_legacy_preference_is_left_in_place()
    {
        await File.WriteAllTextAsync(Legacy.SettingsFile, new string(' ', 9000));

        Assert.False(SensorDefinitionLocation.MigrateLegacy(Current, () => Legacy));

        Assert.False(File.Exists(Current.SettingsFile));
        Assert.True(File.Exists(Legacy.SettingsFile));
    }

    [Fact]
    public async Task Read_only_installation_folder_still_moves_the_preference()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows folder permissions need ACLs; the Unix mode covers /Applications.
            return;
        }

        await File.WriteAllTextAsync(Legacy.SettingsFile, CustomPreference);
        Directory.CreateDirectory(Legacy.DefinitionsDirectory);
        File.SetUnixFileMode(Applications, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (TryCreate(Path.Combine(Applications, "probe")))
            {
                // Running as root ignores the mode, so there's nothing to check.
                return;
            }

            Assert.True(SensorDefinitionLocation.MigrateLegacy(Current, () => Legacy));
            Assert.Equal(CustomPreference, await File.ReadAllTextAsync(Current.SettingsFile));
            Assert.True(File.Exists(Legacy.SettingsFile));

            // A later start retries the clean-up without touching the moved preference.
            Assert.False(SensorDefinitionLocation.MigrateLegacy(Current, () => Legacy));
            Assert.Equal(CustomPreference, await File.ReadAllTextAsync(Current.SettingsFile));
        }
        finally
        {
            File.SetUnixFileMode(Applications, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Same_location_is_not_treated_as_legacy()
    {
        File.WriteAllText(Current.SettingsFile, CustomPreference);
        Directory.CreateDirectory(Current.DefinitionsDirectory);

        Assert.False(SensorDefinitionLocation.MigrateLegacy(Current, () => Current));

        Assert.True(File.Exists(Current.SettingsFile));
        Assert.True(Directory.Exists(Current.DefinitionsDirectory));
    }

    [Fact]
    public async Task Sign_out_reset_keeps_the_preference_but_not_the_definitions()
    {
        await File.WriteAllTextAsync(Current.SettingsFile, CustomPreference);
        Directory.CreateDirectory(Current.DefinitionsDirectory);
        await File.WriteAllTextAsync(Path.Combine(DataDirectory, "settings.json"), "{}");

        SensorDefinitionLocation.ResetKeepingPreference(Current,
            () => DesktopNMS.Core.Configuration.LocalDataReset.Wipe(DataDirectory, cacheDirectory: null));

        Assert.Equal(CustomPreference, await File.ReadAllTextAsync(Current.SettingsFile));
        Assert.False(Directory.Exists(Current.DefinitionsDirectory));
        Assert.False(File.Exists(Path.Combine(DataDirectory, "settings.json")));
    }

    [Fact]
    public void Sign_out_reset_without_a_preference_creates_none()
    {
        var ran = false;
        SensorDefinitionLocation.ResetKeepingPreference(Current, () => ran = true);
        Assert.True(ran);
        Assert.False(File.Exists(Current.SettingsFile));
    }

    private static bool TryCreate(string path)
    {
        try
        {
            File.WriteAllText(path, "");
            File.Delete(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

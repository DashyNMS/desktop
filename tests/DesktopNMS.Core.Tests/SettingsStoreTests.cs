using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Always against a temporary file - never the real per-user settings.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dashynms-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_folder);
    }

    private string SettingsFile => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Save_writes_the_file_even_with_nothing_listening()
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        store.Current.Theme = AppTheme.Light;

        store.Save();

        Assert.True(File.Exists(SettingsFile));
        var reloaded = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        Assert.Equal(AppTheme.Light, reloaded.Current.Theme);
    }

    [Fact]
    public void Save_raises_Changed_after_writing()
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        var fileExistedWhenRaised = false;
        store.Changed += (_, _) => fileExistedWhenRaised = File.Exists(SettingsFile);

        store.Save();

        Assert.True(fileExistedWhenRaised);
    }
}

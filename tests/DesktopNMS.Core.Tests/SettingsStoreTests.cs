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

    [Fact]
    public void A_fresh_install_gets_the_shipped_defaults()
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);

        Assert.True(store.Current.JigglePhysicsOnMaps);
        Assert.Equal(AppTheme.System, store.Current.Theme);
        Assert.True(store.Current.ServerTimestampsAreUtc);
        Assert.True(store.Current.ShowRecentlyViewedDevices);
        Assert.Equal(5, store.Current.RecentlyViewedDeviceCount);
        Assert.Equal(DeviceNameStyle.SysName, store.Current.DeviceNameStyle);
    }

    [Fact]
    public void An_existing_file_keeps_the_values_it_was_saved_with()
    {
        File.WriteAllText(SettingsFile, """
            {
              "jigglePhysicsOnMaps": false,
              "theme": "Dark",
              "serverTimestampsAreUtc": false,
              "recentlyViewedDeviceCount": 10,
              "deviceNameStyle": "Hostname"
            }
            """);

        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);

        Assert.False(store.Current.JigglePhysicsOnMaps);
        Assert.Equal(AppTheme.Dark, store.Current.Theme);
        Assert.False(store.Current.ServerTimestampsAreUtc);
        Assert.Equal(10, store.Current.RecentlyViewedDeviceCount);
        Assert.Equal(DeviceNameStyle.Hostname, store.Current.DeviceNameStyle);
    }
}

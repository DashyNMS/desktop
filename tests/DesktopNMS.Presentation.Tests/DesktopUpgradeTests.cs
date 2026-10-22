using System.Security.Cryptography;
using System.Text;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Security;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class DesktopUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dashy-upgrade-" + Guid.NewGuid().ToString("N"));
    public DesktopUpgradeTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("2.0.0", false, false)]
    [InlineData("2.0.0-preview.1", false, false)]
    [InlineData("2.0.0-dev.1", false, true)]
    [InlineData("2.0.0", true, true)]
    public void Release_upgrades_use_the_existing_Windows_profile_and_development_stays_isolated(string version, bool debug, bool development)
    {
        Assert.Equal(development, DesktopProfile.IsDevelopment(version, debug));
        Assert.Equal(development ? Path.Combine(_root, "avalonia") : _root, DesktopProfile.ForBuild(_root, true, development));
        Assert.Equal(Path.Combine(_root, "avalonia"), DesktopProfile.ForBuild(_root, false, development));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    public void Existing_settings_layout_filters_and_main_window_survive_the_upgrade(string version)
    {
        // These names and the separate main-window shape were checked against
        // the shipped v1.0.0 and v1.1.0 SettingsStore/AppSettings sources.
        var json = """
            {"serverUrl":"https://librenms.example.net/","lastRunVersion":"VERSION",
             "theme":"Light","startupTab":"Devices","minimiseToTrayOnClose":true,
             "filter":{"showCritical":true,"showWarning":false,"showAcknowledged":false,"searchText":"example"},
             "window":{"left":60,"top":80,"width":1200,"height":800,"maximised":true},
             "dashboardWidgets":[{"id":"saved-layout","widgetType":"Alerts","title":"Saved alerts","column":2,"row":3,"columnSpan":10,"rowSpan":6}]}
            """.Replace("VERSION", version);
        var path = Path.Combine(_root, "settings.json"); File.WriteAllText(path, json);
        var settings = new SettingsStore(NullLogger<SettingsStore>.Instance, path); settings.Load();
        Assert.Equal("https://librenms.example.net/", settings.Current.ServerUrl);
        Assert.Equal(version, settings.Current.LastRunVersion); Assert.Equal(AppTheme.Light, settings.Current.Theme);
        Assert.False(settings.Current.Filter.ShowWarning); Assert.False(settings.Current.Filter.ShowAcknowledged);
        Assert.Equal("example", settings.Current.Filter.SearchText);
        var widget = Assert.Single(settings.Current.DashboardWidgets);
        Assert.Equal("saved-layout", widget.Id); Assert.Equal("Saved alerts", widget.Title); Assert.Equal(2, widget.Column);
        Assert.True(DesktopProfile.RestoreLegacyWindow(settings.Current));
        var placement = settings.Current.WindowPlacements["MainWindow"];
        Assert.Equal(1200, placement.Width); Assert.Equal(800, placement.Height); Assert.True(placement.Maximised);
        Assert.NotSame(settings.Current.Window, placement); Assert.False(DesktopProfile.RestoreLegacyWindow(settings.Current));
        settings.SaveQuietly();
        var reloaded = new SettingsStore(NullLogger<SettingsStore>.Instance, path).Load();
        Assert.Equal(1200, reloaded.WindowPlacements["MainWindow"].Width);
        Assert.Equal("saved-layout", Assert.Single(reloaded.DashboardWidgets).Id);
    }

    [Theory]
    [InlineData("DesktopNMS.LibreNMS.ApiToken.v1")]
    [InlineData("DesktopNMS.Unimus.ApiToken.v1")]
    [InlineData("DesktopNMS.Graylog.Password.v1")]
    [InlineData("DesktopNMS.ChatGpt.Credentials.v1")]
    public void The_new_store_reads_legacy_DPAPI_files_without_rewriting_them(string purpose)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "secret.dat");
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes("example-secret"), Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, bytes);
        var store = new ProtectedSecretFile(() => path, purpose, "test", new DpapiSecretProtector(), NullLogger.Instance);
        Assert.Equal("example-secret", Encoding.UTF8.GetString(store.Load()!));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_second_instance_signals_the_owner_and_the_profile_can_reopen_after_exit()
    {
        using var ready = new ManualResetEventSlim(); using var stop = new ManualResetEventSlim();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? error = null;
        var owner = new Thread(() =>
        {
            try
            {
                using var instance = new DesktopInstance(_root, false);
                Assert.True(instance.TryClaim(() => shown.TrySetResult()));
                ready.Set(); stop.Wait();
            }
            catch (Exception ex) { error = ex; ready.Set(); }
        });
        owner.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5))); Assert.Null(error);
            using var second = new DesktopInstance(_root, false);
            Assert.False(second.TryClaim(() => { })); second.SignalExisting();
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { stop.Set(); Assert.True(owner.Join(TimeSpan.FromSeconds(5))); }
        Assert.Null(error);
        using var reopened = new DesktopInstance(_root, false);
        Assert.True(reopened.TryClaim(() => { }));
    }
}

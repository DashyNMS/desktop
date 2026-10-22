using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class WindowsInstallerUpgradeTests
{
    [InstallerUpgradeFact]
    public void The_installed_app_preserves_the_legacy_profile_and_encrypted_secrets()
    {
        Assert.True(OperatingSystem.IsWindows());
        var profile=AppPaths.DefaultDataDirectory;
        var seed=Environment.GetEnvironmentVariable("DASHYNMS_UPGRADE_PHASE") == "seed";
        var from=Environment.GetEnvironmentVariable("DASHYNMS_UPGRADE_FROM")!;
        var executable=Environment.GetEnvironmentVariable("DASHYNMS_UPGRADE_EXE")!;
        var expected=seed ? from : Environment.GetEnvironmentVariable("DASHYNMS_UPGRADE_VERSION")!;
        var info=FileVersionInfo.GetVersionInfo(executable);
        Assert.StartsWith(expected,info.ProductVersion);
        Assert.Equal("DashyNMS",info.FileDescription);
        Assert.Equal(profile,DesktopProfile.ForBuild(profile,true,false));
        var secrets=new[] {
            ("token.dat","DesktopNMS.LibreNMS.ApiToken.v1"),
            ("unimus-token.dat","DesktopNMS.Unimus.ApiToken.v1"),
            ("graylog-password.dat","DesktopNMS.Graylog.Password.v1") };
        if(seed)
        {
            Assert.False(Directory.Exists(profile),"Installer upgrade checks require a fresh CI account.");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile,"settings.json"),"""
                {"serverUrl":"https://upgrade.example.net/","lastRunVersion":"VERSION",
                 "filter":{"showWarning":false,"searchText":"upgrade-marker"},
                 "window":{"left":60,"top":80,"width":1200,"height":800,"maximised":true},
                 "dashboardWidgets":[{"id":"upgrade-layout","widgetType":"Alerts","title":"Preserved layout","column":2,"row":3,"columnSpan":10,"rowSpan":6}]}
                """.Replace("VERSION",from));
            foreach(var (file,purpose) in secrets)
                File.WriteAllBytes(Path.Combine(profile,file),ProtectedData.Protect(Encoding.UTF8.GetBytes("example-upgrade-secret"),Encoding.UTF8.GetBytes(purpose),DataProtectionScope.CurrentUser));
            return;
        }
        var settings=new SettingsStore(NullLogger<SettingsStore>.Instance,Path.Combine(profile,"settings.json"));
        settings.Load();
        Assert.Equal(from,settings.Current.LastRunVersion);
        Assert.Equal("upgrade-marker",settings.Current.Filter.SearchText);
        Assert.False(settings.Current.Filter.ShowWarning);
        Assert.Equal("upgrade-layout",Assert.Single(settings.Current.DashboardWidgets).Id);
        Assert.True(DesktopProfile.RestoreLegacyWindow(settings.Current));
        Assert.Equal(1200,settings.Current.WindowPlacements["MainWindow"].Width);
        Assert.True(settings.Current.WindowPlacements["MainWindow"].Maximised);
        foreach(var (file,purpose) in secrets)
        {
            var store=new ProtectedSecretFile(()=>Path.Combine(profile,file),purpose,"upgrade test",new DpapiSecretProtector(),NullLogger.Instance);
            Assert.Equal("example-upgrade-secret",Encoding.UTF8.GetString(store.Load()!));
        }
        settings.SaveQuietly();
        Assert.False(DesktopProfile.RestoreLegacyWindow(settings.Load()));
    }
}
public sealed class InstallerUpgradeFactAttribute : FactAttribute
{
    public InstallerUpgradeFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DASHYNMS_UPGRADE_PHASE")))
            Skip="Installer integration checks run only on disposable Windows CI accounts.";
    }
}

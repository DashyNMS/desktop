using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Updates;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AppThemeTests
{
    [Theory]
    [InlineData(AppTheme.Dark, true, AppTheme.Dark)]
    [InlineData(AppTheme.Dark, false, AppTheme.Dark)]
    [InlineData(AppTheme.Light, false, AppTheme.Light)]
    [InlineData(AppTheme.System, true, AppTheme.Light)]
    [InlineData(AppTheme.System, false, AppTheme.Dark)]
    public void Match_Windows_follows_the_Windows_app_mode_and_the_others_ignore_it(AppTheme setting, bool windowsLight, AppTheme expected)
    {
        Assert.Equal(expected, setting.Resolve(windowsLight));
    }

    [Fact]
    public void The_alerts_tab_count_is_on_by_default_including_acknowledged_and_survives_a_clone()
    {
        var settings = new AppSettings();
        Assert.True(settings.ShowAlertTabBadge);
        Assert.True(settings.AlertTabBadgeIncludesAcknowledged);

        settings.ShowAlertTabBadge = false;
        settings.AlertTabBadgeIncludesAcknowledged = false;
        var clone = settings.Clone();
        Assert.False(clone.ShowAlertTabBadge);
        Assert.False(clone.AlertTabBadgeIncludesAcknowledged);
    }

    [Fact]
    public void Pinned_devices_are_on_by_default_and_survive_a_clone()
    {
        var settings = new AppSettings();
        Assert.True(settings.EnablePinnedDevices);

        settings.EnablePinnedDevices = false;
        Assert.False(settings.Clone().EnablePinnedDevices);
    }
}

public class BugReportLinkTests
{
    [Fact]
    public void The_link_opens_a_new_issue_with_the_versions_filled_in()
    {
        var link = BugReportLink.Build("1.1.0", "Microsoft Windows 10.0.26200", "24.9.1");
        var body = Uri.UnescapeDataString(link.Query[(link.Query.IndexOf("body=", StringComparison.Ordinal) + 5)..]);

        Assert.StartsWith(BugReportLink.NewIssueUrl, link.ToString(), StringComparison.Ordinal);
        Assert.Contains("DashyNMS: 1.1.0", body, StringComparison.Ordinal);
        Assert.Contains("Windows: Microsoft Windows 10.0.26200", body, StringComparison.Ordinal);
        Assert.Contains("LibreNMS: 24.9.1", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    public void Without_a_server_version_it_says_not_connected(string? version)
    {
        var link = BugReportLink.Build("1.1.0", "Windows", version);

        Assert.Contains("LibreNMS: not connected", Uri.UnescapeDataString(link.Query), StringComparison.Ordinal);
    }

    [Fact]
    public void Values_can_not_add_their_own_lines()
    {
        var link = BugReportLink.Build("1.1.0\nExtra: injected", "Windows", "24.9");

        Assert.DoesNotContain("\nExtra:", Uri.UnescapeDataString(link.Query), StringComparison.Ordinal);
    }
}

using System;
using DesktopNMS.Core.Alerting;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class TrayStatusTests
{
    [Theory]
    [InlineData(0, false, false, TrayIconKind.AllClear)]
    [InlineData(4, false, false, TrayIconKind.Warning)]
    [InlineData(6, true, false, TrayIconKind.Critical)]
    [InlineData(0, false, true, TrayIconKind.Backup)]
    [InlineData(1, false, true, TrayIconKind.Warning)]
    [InlineData(1, true, true, TrayIconKind.Critical)]
    public void Alerts_outrank_the_backup_address_on_the_icon(int badge, bool badgeIsCritical, bool onBackup, TrayIconKind expected)
    {
        var state = TrayStatus.Describe(TrayConnection.Connected, onBackup, 0, 0, badge, badgeIsCritical);

        Assert.Equal(expected, state.Icon);
        Assert.Equal(badge, state.BadgeCount);
    }

    [Fact]
    public void Acknowledged_alerts_counted_by_the_badge_still_show_the_dot()
    {
        // Nothing active, but the Alerts tab badge includes two acknowledged.
        Assert.Equal(TrayIconKind.Warning, TrayStatus.Describe(TrayConnection.Connected, false, 0, 0, 2, false).Icon);
    }

    [Theory]
    [InlineData(TrayConnection.SignedOut)]
    [InlineData(TrayConnection.SigningIn)]
    [InlineData(TrayConnection.Unreachable)]
    [InlineData(TrayConnection.TokenRejected)]
    public void Without_a_working_connection_the_icon_is_grey_whatever_the_counts(TrayConnection connection)
    {
        var state = TrayStatus.Describe(connection, onBackup: true, critical: 3, warning: 5, badgeCount: 8, badgeIsCritical: true);

        Assert.Equal(0, state.BadgeCount);

        Assert.Equal(TrayIconKind.NotConnected, state.Icon);
        Assert.DoesNotContain("critical", state.Tooltip);
    }

    [Fact]
    public void Connected_says_when_it_last_checked()
    {
        var state = TrayStatus.Describe(TrayConnection.Connected, false, 0, 0, 0, false, TimeSpan.FromSeconds(12));

        Assert.Equal("Connected", state.Title);
        Assert.Equal("Checked 12s ago", state.Detail);
        Assert.Equal("DashyNMS - no active alerts", state.Tooltip);
    }

    [Fact]
    public void Before_the_first_check_it_says_so()
    {
        Assert.Equal("Waiting for the first check", TrayStatus.Describe(TrayConnection.Connected, false, 0, 0, 0, false).Detail);
    }

    [Fact]
    public void The_backup_address_shows_in_the_header_and_tooltip()
    {
        var state = TrayStatus.Describe(TrayConnection.Connected, true, 3, 5, 8, true);

        Assert.Equal("On the backup address", state.Title);
        Assert.Equal("DashyNMS - 3 critical, 5 warning (backup address)", state.Tooltip);
        Assert.True(state.Tooltip.Length <= TrayStatus.MaxTooltipLength);
    }

    [Fact]
    public void Unreachable_counts_down_to_the_next_try()
    {
        Assert.Equal("Trying again in 45s", TrayStatus.Describe(TrayConnection.Unreachable, false, 0, 0, 0, false, nextCheck: "45s").Detail);
        Assert.Equal("Trying again shortly", TrayStatus.Describe(TrayConnection.Unreachable, false, 0, 0, 0, false).Detail);
    }

    [Fact]
    public void The_tooltip_never_exceeds_the_shell_limit()
    {
        var state = TrayStatus.Describe(TrayConnection.Connected, true, 123456789, 987654321, 5, true);

        Assert.Equal(TrayStatus.MaxTooltipLength, state.Tooltip.Length);
    }

    [Theory]
    [InlineData(2, "just now")]
    [InlineData(12, "12s ago")]
    [InlineData(185, "3m ago")]
    [InlineData(7300, "2h ago")]
    public void Ages_read_short(int seconds, string expected)
    {
        Assert.Equal(expected, TrayStatus.FormatAgo(TimeSpan.FromSeconds(seconds)));
    }
}

using System;
using DesktopNMS.Core.Alerting;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class TrayStatusTests
{
    [Theory]
    [InlineData(0, 0, false, TrayIconKind.AllClear)]
    [InlineData(0, 4, false, TrayIconKind.Warning)]
    [InlineData(2, 4, false, TrayIconKind.Critical)]
    [InlineData(0, 0, true, TrayIconKind.Backup)]
    [InlineData(0, 1, true, TrayIconKind.Warning)]
    [InlineData(1, 0, true, TrayIconKind.Critical)]
    public void Alerts_outrank_the_backup_address_on_the_icon(int critical, int warning, bool onBackup, TrayIconKind expected)
    {
        Assert.Equal(expected, TrayStatus.Describe(TrayConnection.Connected, onBackup, critical, warning).Icon);
    }

    [Theory]
    [InlineData(TrayConnection.SignedOut)]
    [InlineData(TrayConnection.SigningIn)]
    [InlineData(TrayConnection.Unreachable)]
    [InlineData(TrayConnection.TokenRejected)]
    public void Without_a_working_connection_the_icon_is_grey_whatever_the_counts(TrayConnection connection)
    {
        var state = TrayStatus.Describe(connection, onBackup: true, critical: 3, warning: 5);

        Assert.Equal(TrayIconKind.NotConnected, state.Icon);
        Assert.DoesNotContain("critical", state.Tooltip);
    }

    [Fact]
    public void Connected_says_when_it_last_checked()
    {
        var state = TrayStatus.Describe(TrayConnection.Connected, false, 0, 0, TimeSpan.FromSeconds(12));

        Assert.Equal("Connected", state.Title);
        Assert.Equal("Checked 12s ago", state.Detail);
        Assert.Equal("DashyNMS - no active alerts", state.Tooltip);
    }

    [Fact]
    public void Before_the_first_check_it_says_so()
    {
        Assert.Equal("Waiting for the first check", TrayStatus.Describe(TrayConnection.Connected, false, 0, 0).Detail);
    }

    [Fact]
    public void The_backup_address_shows_in_the_header_and_tooltip()
    {
        var state = TrayStatus.Describe(TrayConnection.Connected, true, 3, 5);

        Assert.Equal("On the backup address", state.Title);
        Assert.Equal("DashyNMS - 3 critical, 5 warning (backup address)", state.Tooltip);
        Assert.True(state.Tooltip.Length <= TrayStatus.MaxTooltipLength);
    }

    [Fact]
    public void Unreachable_counts_down_to_the_next_try()
    {
        Assert.Equal("Trying again in 45s", TrayStatus.Describe(TrayConnection.Unreachable, false, 0, 0, nextCheck: "45s").Detail);
        Assert.Equal("Trying again shortly", TrayStatus.Describe(TrayConnection.Unreachable, false, 0, 0).Detail);
    }

    [Fact]
    public void The_tooltip_never_exceeds_the_shell_limit()
    {
        var state = TrayStatus.Describe(TrayConnection.Connected, true, 123456789, 987654321);

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

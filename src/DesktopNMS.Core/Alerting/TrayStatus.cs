using System;
using System.Globalization;

namespace DesktopNMS.Core.Alerting;

/// <summary>Where the app stands with its server, as far as the tray is concerned.</summary>
public enum TrayConnection
{
    SignedOut,
    SigningIn,
    Connected,

    /// <summary>Signed in, but the last refresh failed.</summary>
    Unreachable,

    /// <summary>Signed in, but the server turned the API token down.</summary>
    TokenRejected,
}

/// <summary>What the tray icon shows (#232).</summary>
public enum TrayIconKind
{
    /// <summary>The DashyNMS mark: connected, nothing active.</summary>
    AllClear,

    /// <summary>A solid amber dot with the count: alerts, nothing critical.</summary>
    Warning,

    /// <summary>A solid red dot with the count: anything critical.</summary>
    Critical,

    /// <summary>The mark with a small amber dot: on the backup address, nothing active.</summary>
    Backup,

    /// <summary>The mark in grey: signed out, signing in, or the server isn't answering.</summary>
    NotConnected,
}

/// <summary>The tray icon, tooltip and menu header for a given state.</summary>
/// <param name="BadgeCount">The number in the amber or red dot; 0 otherwise.</param>
public sealed record TrayState(TrayIconKind Icon, string Title, string Detail, string Tooltip, int BadgeCount = 0);

/// <summary>
/// Turns the app's connection and alert counts into what the tray shows
/// (#232), kept out of the WinForms/WPF code so it can be tested.
/// </summary>
public static class TrayStatus
{
    /// <summary>NotifyIcon.Text throws above 63 characters on some Windows builds.</summary>
    public const int MaxTooltipLength = 63;

    /// <param name="badgeCount">
    /// What the Alerts tab's badge counts - active alerts, and acknowledged
    /// ones if Settings says so - for the dot and its number.
    /// </param>
    /// <param name="badgeIsCritical">Any of those is critical: red rather than amber.</param>
    /// <param name="lastChecked">How long ago the last refresh finished, if one has.</param>
    /// <param name="nextCheck">A short countdown to the next refresh, such as "45s".</param>
    public static TrayState Describe(
        TrayConnection connection,
        bool onBackup,
        int critical,
        int warning,
        int badgeCount,
        bool badgeIsCritical,
        TimeSpan? lastChecked = null,
        string? nextCheck = null)
    {
        switch (connection)
        {
            case TrayConnection.SignedOut:
                return new(TrayIconKind.NotConnected, "Signed out", "Sign in to see alerts", "DashyNMS - signed out");

            case TrayConnection.SigningIn:
                return new(TrayIconKind.NotConnected, "Signing in…", "Loading alerts", "DashyNMS - signing in");

            case TrayConnection.TokenRejected:
                return new(TrayIconKind.NotConnected, "Sign in again", "The server turned the API token down", "DashyNMS - sign in again");

            case TrayConnection.Unreachable:
                return new(
                    TrayIconKind.NotConnected,
                    "Can't reach the server",
                    string.IsNullOrWhiteSpace(nextCheck) ? "Trying again shortly" : $"Trying again in {nextCheck}",
                    "DashyNMS - can't reach the server");
        }

        var icon = badgeCount > 0
            ? badgeIsCritical ? TrayIconKind.Critical : TrayIconKind.Warning
            : onBackup ? TrayIconKind.Backup : TrayIconKind.AllClear;

        var title = onBackup ? "On the backup address" : "Connected";
        var detail = onBackup
            ? "The main address isn't answering"
            : lastChecked is { } ago ? "Checked " + FormatAgo(ago) : "Waiting for the first check";

        var tooltip = critical + warning == 0
            ? "DashyNMS - no active alerts"
            : $"DashyNMS - {critical} critical, {warning} warning";
        if (onBackup)
        {
            tooltip += " (backup address)";
        }

        if (tooltip.Length > MaxTooltipLength)
        {
            tooltip = tooltip[..MaxTooltipLength];
        }

        return new(icon, title, detail, tooltip, Math.Max(0, badgeCount));
    }

    /// <summary>"just now", "12s ago", "4m ago", "2h ago".</summary>
    public static string FormatAgo(TimeSpan ago)
    {
        if (ago < TimeSpan.FromSeconds(5))
        {
            return "just now";
        }

        if (ago < TimeSpan.FromMinutes(1))
        {
            return ((int)ago.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s ago";
        }

        if (ago < TimeSpan.FromHours(1))
        {
            return ((int)ago.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m ago";
        }

        return ((int)ago.TotalHours).ToString(CultureInfo.InvariantCulture) + "h ago";
    }
}

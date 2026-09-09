using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Configuration;

/// <summary>
/// Which tab to show when DashyNMS starts. Mirrors DesktopNMS.ViewModels.MainTab,
/// duplicated here rather than referenced so this Core project does not depend
/// on the WPF view-model project.
/// </summary>
public enum StartupTab
{
    Dashboard,
    Devices,
    Health,
    Alerts,
}

/// <summary>
/// Everything DesktopNMS persists between runs, apart from the API token which
/// is encrypted separately by <see cref="Security.ITokenProtector"/>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Root URL of the LibreNMS web UI, e.g. https://nms.example.com/.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Accept self-signed or internally-issued certificates.</summary>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary>Per-request HTTP timeout.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>How often to poll for alerts, in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Keep the API token on this machine (DPAPI-encrypted) so sign-in is not needed each launch.</summary>
    public bool RememberToken { get; set; } = true;

    /// <summary>
    /// True if the LibreNMS server stores alert timestamps in UTC. Most installs
    /// use server-local time, which is the default here.
    /// </summary>
    public bool ServerTimestampsAreUtc { get; set; }

    /// <summary>Also fetch alerts in state 0 (recovered) so they can be shown and can raise recovery toasts.</summary>
    public bool IncludeRecoveredAlerts { get; set; }

    /// <summary>Which device name to show in the alert list.</summary>
    public DeviceNameStyle DeviceNameStyle { get; set; } = DeviceNameStyle.Hostname;

    /// <summary>
    /// Fetch the faults (the rows the rule matched) for the selected alert.
    /// Costs one extra request per selection, so it can be turned off.
    /// </summary>
    public bool LoadAlertFaults { get; set; } = true;

    /// <summary>Closing the window hides it to the notification area instead of exiting.</summary>
    public bool MinimiseToTrayOnClose { get; set; } = true;

    /// <summary>Start hidden in the notification area.</summary>
    public bool StartMinimised { get; set; }

    /// <summary>Register a Run key so DesktopNMS starts at sign-in.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Which tab is showing when the main window first appears.</summary>
    public StartupTab StartupTab { get; set; } = StartupTab.Alerts;

    /// <summary>
    /// The newest release tag DashyNMS has already shown an update toast for,
    /// so the same "update available" notice does not repeat on every launch.
    /// </summary>
    public string? LastNotifiedUpdateVersion { get; set; }

    public NotificationSettings Notifications { get; set; } = new();

    public AlertFilterSettings Filter { get; set; } = new();

    /// <summary>Warning/critical bands applied to dBm sensors on the Health tab.</summary>
    public DbmThresholdSettings DbmThresholds { get; set; } = new();

    public WindowPlacement? Window { get; set; }

    public AppSettings Clone() => new()
    {
        ServerUrl = ServerUrl,
        AllowUntrustedCertificate = AllowUntrustedCertificate,
        TimeoutSeconds = TimeoutSeconds,
        PollIntervalSeconds = PollIntervalSeconds,
        RememberToken = RememberToken,
        ServerTimestampsAreUtc = ServerTimestampsAreUtc,
        IncludeRecoveredAlerts = IncludeRecoveredAlerts,
        DeviceNameStyle = DeviceNameStyle,
        LoadAlertFaults = LoadAlertFaults,
        MinimiseToTrayOnClose = MinimiseToTrayOnClose,
        StartMinimised = StartMinimised,
        StartWithWindows = StartWithWindows,
        StartupTab = StartupTab,
        LastNotifiedUpdateVersion = LastNotifiedUpdateVersion,
        Notifications = Notifications.Clone(),
        Filter = Filter.Clone(),
        DbmThresholds = DbmThresholds.Clone(),
        Window = Window?.Clone(),
    };

    /// <summary>Clamps anything a hand-edited settings file could have made nonsensical.</summary>
    public void Normalise()
    {
        if (TimeoutSeconds < 5) TimeoutSeconds = 5;
        if (TimeoutSeconds > 300) TimeoutSeconds = 300;
        if (PollIntervalSeconds < 15) PollIntervalSeconds = 15;
        if (PollIntervalSeconds > 3600) PollIntervalSeconds = 3600;

        Notifications ??= new NotificationSettings();
        Filter ??= new AlertFilterSettings();
        DbmThresholds ??= new DbmThresholdSettings();
        Notifications.Normalise();
        DbmThresholds.Normalise();
    }
}

/// <summary>
/// The dBm bands used to colour sensors on the Health tab. Optical power
/// readings run negative, so weaker signal means more negative: warning and
/// critical sit below the healthy range, not above it.
/// </summary>
public sealed class DbmThresholdSettings
{
    /// <summary>At or below this (and above <see cref="CriticalThreshold"/>) is a warning.</summary>
    public double WarningThreshold { get; set; } = -12.5;

    /// <summary>At or below this is critical (signal too weak).</summary>
    public double CriticalThreshold { get; set; } = -14;

    /// <summary>
    /// At or above this, the reading is treated as "no data" (an unplugged
    /// port reporting a nonsensical high value) and is not flagged at all.
    /// </summary>
    public double IgnoreAtOrAbove { get; set; } = 39;

    /// <summary>
    /// At or below this, the reading is also treated as "no data": some
    /// hardware reports a fixed low sentinel (e.g. -39 dBm) for an unplugged
    /// or down port instead of a genuinely weak signal.
    /// </summary>
    public double IgnoreAtOrBelow { get; set; } = -39;

    public DbmThresholdSettings Clone() => new()
    {
        WarningThreshold = WarningThreshold,
        CriticalThreshold = CriticalThreshold,
        IgnoreAtOrAbove = IgnoreAtOrAbove,
        IgnoreAtOrBelow = IgnoreAtOrBelow,
    };

    /// <summary>Recovers a sane ordering if a hand-edited settings file breaks it.</summary>
    public void Normalise()
    {
        if (CriticalThreshold > WarningThreshold || WarningThreshold >= IgnoreAtOrAbove || IgnoreAtOrBelow >= CriticalThreshold)
        {
            WarningThreshold = -12.5;
            CriticalThreshold = -14;
            IgnoreAtOrAbove = 39;
            IgnoreAtOrBelow = -39;
        }
    }

    /// <summary>Classifies a dBm reading using these bands.</summary>
    public AlertSeverity Evaluate(double value)
    {
        if (value >= IgnoreAtOrAbove || value <= IgnoreAtOrBelow)
        {
            return AlertSeverity.Unknown;
        }

        if (value <= CriticalThreshold)
        {
            return AlertSeverity.Critical;
        }

        if (value <= WarningThreshold)
        {
            return AlertSeverity.Warning;
        }

        return AlertSeverity.Ok;
    }
}

/// <summary>How long a Windows toast stays on screen.</summary>
public enum ToastPersistence
{
    /// <summary>Standard toast: shows for a few seconds, then moves to the notification centre.</summary>
    Transient = 0,

    /// <summary>Stays on screen until the user dismisses or acts on it (toast scenario "Reminder").</summary>
    UntilDismissed = 1,

    /// <summary>Stays on screen and loops an alarm sound (toast scenario "Alarm").</summary>
    UntilDismissedWithAlarm = 2,
}

/// <summary>Per-severity notification behaviour.</summary>
public sealed class SeverityNotificationSettings
{
    public bool Enabled { get; set; } = true;

    public ToastPersistence Persistence { get; set; } = ToastPersistence.Transient;

    public bool PlaySound { get; set; } = true;

    public SeverityNotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Persistence = Persistence,
        PlaySound = PlaySound,
    };
}

/// <summary>Notification settings, including the per-severity "stickiness".</summary>
public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Critical alerts stay on screen by default.</summary>
    public SeverityNotificationSettings Critical { get; set; } = new()
    {
        Enabled = true,
        Persistence = ToastPersistence.UntilDismissed,
        PlaySound = true,
    };

    /// <summary>Warnings are transient by default.</summary>
    public SeverityNotificationSettings Warning { get; set; } = new()
    {
        Enabled = true,
        Persistence = ToastPersistence.Transient,
        PlaySound = true,
    };

    /// <summary>Alerts from rules with severity "ok", which are rare.</summary>
    public SeverityNotificationSettings Ok { get; set; } = new()
    {
        Enabled = false,
        Persistence = ToastPersistence.Transient,
        PlaySound = false,
    };

    /// <summary>Toast when an alert clears.</summary>
    public bool NotifyOnRecovery { get; set; } = true;

    /// <summary>Toast when an alert is acknowledged (usually by someone else).</summary>
    public bool NotifyOnAcknowledge { get; set; }

    /// <summary>
    /// Do not toast for everything already outstanding when the app starts.
    /// Only alerts that appear after the first successful poll raise a toast.
    /// </summary>
    public bool SuppressOnFirstPoll { get; set; } = true;

    /// <summary>
    /// Above this many new alerts in one poll, collapse them into a single
    /// summary toast rather than flooding the notification centre.
    /// </summary>
    public int MaxToastsPerPoll { get; set; } = 5;

    /// <summary>Suppress toasts between <see cref="QuietHoursStartHour"/> and <see cref="QuietHoursEndHour"/>.</summary>
    public bool QuietHoursEnabled { get; set; }

    /// <summary>Local hour (0-23) at which quiet hours begin.</summary>
    public int QuietHoursStartHour { get; set; } = 22;

    /// <summary>Local hour (0-23) at which quiet hours end.</summary>
    public int QuietHoursEndHour { get; set; } = 7;

    /// <summary>Critical alerts still toast during quiet hours.</summary>
    public bool QuietHoursAllowCritical { get; set; } = true;

    public SeverityNotificationSettings ForSeverity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => Critical,
        AlertSeverity.Warning => Warning,
        _ => Ok,
    };

    /// <summary>True if a toast should be suppressed right now because of quiet hours.</summary>
    public bool IsInQuietHours(DateTime localNow, AlertSeverity severity)
    {
        if (!QuietHoursEnabled)
        {
            return false;
        }

        if (QuietHoursAllowCritical && severity == AlertSeverity.Critical)
        {
            return false;
        }

        var hour = localNow.Hour;

        // A window that wraps past midnight, e.g. 22:00 -> 07:00.
        return QuietHoursStartHour <= QuietHoursEndHour
            ? hour >= QuietHoursStartHour && hour < QuietHoursEndHour
            : hour >= QuietHoursStartHour || hour < QuietHoursEndHour;
    }

    public void Normalise()
    {
        Critical ??= new SeverityNotificationSettings();
        Warning ??= new SeverityNotificationSettings();
        Ok ??= new SeverityNotificationSettings();

        if (MaxToastsPerPoll < 1) MaxToastsPerPoll = 1;
        if (MaxToastsPerPoll > 25) MaxToastsPerPoll = 25;
        if (QuietHoursStartHour is < 0 or > 23) QuietHoursStartHour = 22;
        if (QuietHoursEndHour is < 0 or > 23) QuietHoursEndHour = 7;
    }

    public NotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Critical = Critical.Clone(),
        Warning = Warning.Clone(),
        Ok = Ok.Clone(),
        NotifyOnRecovery = NotifyOnRecovery,
        NotifyOnAcknowledge = NotifyOnAcknowledge,
        SuppressOnFirstPoll = SuppressOnFirstPoll,
        MaxToastsPerPoll = MaxToastsPerPoll,
        QuietHoursEnabled = QuietHoursEnabled,
        QuietHoursStartHour = QuietHoursStartHour,
        QuietHoursEndHour = QuietHoursEndHour,
        QuietHoursAllowCritical = QuietHoursAllowCritical,
    };
}

/// <summary>The filter chips the user last had selected.</summary>
public sealed class AlertFilterSettings
{
    public bool ShowCritical { get; set; } = true;

    public bool ShowWarning { get; set; } = true;

    public bool ShowOk { get; set; } = true;

    public bool ShowUnknownSeverity { get; set; } = true;

    public bool ShowActive { get; set; } = true;

    public bool ShowAcknowledged { get; set; } = true;

    public bool ShowRecovered { get; set; }

    public string? SearchText { get; set; }

    public AlertFilterSettings Clone() => new()
    {
        ShowCritical = ShowCritical,
        ShowWarning = ShowWarning,
        ShowOk = ShowOk,
        ShowUnknownSeverity = ShowUnknownSeverity,
        ShowActive = ShowActive,
        ShowAcknowledged = ShowAcknowledged,
        ShowRecovered = ShowRecovered,
        SearchText = SearchText,
    };
}

/// <summary>Remembered size and position of the main window.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximised { get; set; }

    public WindowPlacement Clone() => new()
    {
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        Maximised = Maximised,
    };
}

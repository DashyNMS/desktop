using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>One notification to show: what it says and how insistent it is. How it's shown (a Windows toast, a phone notification) is up to the app.</summary>
public sealed record PlannedNotification(
    string Title,
    string Body,
    string? Detail,
    AlertSeverity Severity,
    ToastPersistence Persistence,
    bool PlaySound,
    bool IsProblem,
    AlertChange? Change);

/// <summary>What a poll's alert changes turn into: the notifications to show, or why there are none.</summary>
public sealed class AlertNotificationPlan
{
    public static readonly AlertNotificationPlan Nothing = new(Array.Empty<PlannedNotification>(), isSummary: false, notifiableCount: 0, suppressionReason: null);

    public AlertNotificationPlan(IReadOnlyList<PlannedNotification> notifications, bool isSummary, int notifiableCount, string? suppressionReason)
    {
        Notifications = notifications;
        IsSummary = isSummary;
        NotifiableCount = notifiableCount;
        SuppressionReason = suppressionReason;
    }

    public IReadOnlyList<PlannedNotification> Notifications { get; }

    /// <summary>More changes deserved a notification than <see cref="NotificationSettings.MaxToastsPerPoll"/> allows, so they are rolled into one summary.</summary>
    public bool IsSummary { get; }

    /// <summary>How many changes deserved a notification, before any summarising.</summary>
    public int NotifiableCount { get; }

    /// <summary>Why nothing is shown, in one line for the log - "Why did nothing pop up?" is the most common question about notifications.</summary>
    public string? SuppressionReason { get; }
}

/// <summary>
/// Which alert changes deserve a notification, what each says, and when they
/// are rolled into one summary (#195). Kept free of any UI so desktop and
/// mobile decide the same way - the desktop shows the result as Windows
/// toasts, the phone as its own notifications.
/// </summary>
public static class AlertNotificationPlanner
{
    /// <param name="changes">This poll's alert changes.</param>
    /// <param name="isFirstPoll">The first poll after starting, which <see cref="NotificationSettings.SuppressOnFirstPoll"/> can keep quiet.</param>
    /// <param name="settings">The user's notification settings.</param>
    /// <param name="localNow">Now, in local time - for quiet hours.</param>
    /// <param name="deviceName">Names an alert's device the way the app's alert list does.</param>
    /// <param name="deviceLocation">An alert's device location, for the summary's "+2 more in Rack 3".</param>
    /// <param name="wasSelfInitiated">True for a change this app itself made (acknowledging from the app), which never notifies.</param>
    public static AlertNotificationPlan Plan(
        IReadOnlyList<AlertChange> changes,
        bool isFirstPoll,
        NotificationSettings settings,
        DateTime localNow,
        Func<Alert, string> deviceName,
        Func<Alert, string?>? deviceLocation = null,
        Func<AlertChange, bool>? wasSelfInitiated = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(deviceName);

        if (changes.Count == 0)
        {
            return AlertNotificationPlan.Nothing;
        }

        if (!settings.Enabled)
        {
            return Suppressed("notifications are switched off in settings");
        }

        if (isFirstPoll && settings.SuppressOnFirstPoll)
        {
            return Suppressed("this was the first poll after starting");
        }

        var notifiable = changes.Where(change => ShouldNotify(change, settings, localNow, wasSelfInitiated)).ToList();
        if (notifiable.Count == 0)
        {
            return Suppressed("none notifiable (" + DescribeSuppression(changes, settings, localNow) + ")");
        }

        if (notifiable.Count > settings.MaxToastsPerPoll)
        {
            return new AlertNotificationPlan(new[] { Summary(notifiable, settings, deviceName, deviceLocation) }, isSummary: true, notifiable.Count, suppressionReason: null);
        }

        var planned = notifiable.Select(change => ForChange(change, settings, deviceName)).ToList();
        return new AlertNotificationPlan(planned, isSummary: false, notifiable.Count, suppressionReason: null);
    }

    /// <summary>Whether one change deserves a notification of its own.</summary>
    public static bool ShouldNotify(AlertChange change, NotificationSettings settings, DateTime localNow, Func<AlertChange, bool>? wasSelfInitiated = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(settings);

        // Acknowledging or returning an alert to active from within the app
        // must not notify you about your own action.
        if (change.Kind is AlertChangeKind.Acknowledged or AlertChangeKind.Unacknowledged
            && wasSelfInitiated?.Invoke(change) == true)
        {
            return false;
        }

        var severity = change.Alert.Severity;
        if (settings.IsInQuietHours(localNow, severity))
        {
            return false;
        }

        return change.Kind switch
        {
            AlertChangeKind.New or AlertChangeKind.Reopened or AlertChangeKind.Unacknowledged
                => settings.ForSeverity(severity).Enabled,
            AlertChangeKind.Recovered => settings.NotifyOnRecovery,
            AlertChangeKind.Acknowledged => settings.NotifyOnAcknowledge,
            _ => false,
        };
    }

    /// <summary>"Critical: core-sw-01", "Critical again: ...", "Recovered: ..." and so on.</summary>
    public static string BuildTitle(AlertChange change, string device)
    {
        ArgumentNullException.ThrowIfNull(change);

        return change.Kind switch
        {
            AlertChangeKind.Recovered => $"Recovered: {device}",
            AlertChangeKind.Acknowledged => $"Acknowledged: {device}",
            AlertChangeKind.Unacknowledged => $"Unacknowledged: {device}",
            AlertChangeKind.Reopened => $"{change.Alert.Severity.ToDisplayString()} again: {device}",
            _ => $"{change.Alert.Severity.ToDisplayString()}: {device}",
        };
    }

    /// <summary>Explains, in one line, why a set of changes produced no notification.</summary>
    public static string DescribeSuppression(IReadOnlyList<AlertChange> changes, NotificationSettings settings, DateTime localNow)
    {
        var reasons = new List<string>();

        var quiet = changes.Count(c => settings.IsInQuietHours(localNow, c.Alert.Severity));
        if (quiet > 0)
        {
            reasons.Add($"{quiet} within quiet hours");
        }

        var acknowledged = changes.Count(c => c.Kind == AlertChangeKind.Acknowledged);
        if (acknowledged > 0 && !settings.NotifyOnAcknowledge)
        {
            reasons.Add($"{acknowledged} acknowledged, and 'notify on acknowledge' is off");
        }

        var recovered = changes.Count(c => c.Kind == AlertChangeKind.Recovered);
        if (recovered > 0 && !settings.NotifyOnRecovery)
        {
            reasons.Add($"{recovered} recovered, and 'notify on recovery' is off");
        }

        foreach (var severity in new[] { AlertSeverity.Critical, AlertSeverity.Warning, AlertSeverity.Ok })
        {
            if (settings.ForSeverity(severity).Enabled)
            {
                continue;
            }

            var count = changes.Count(c => c.IsProblem && c.Alert.Severity == severity);
            if (count > 0)
            {
                reasons.Add($"{count} {severity.ToDisplayString().ToLowerInvariant()}, which is switched off");
            }
        }

        return reasons.Count > 0 ? string.Join("; ", reasons) : "no reason recorded";
    }

    /// <summary>The first line of an alert note, trimmed to fit a notification.</summary>
    public static string FirstLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var index = text.IndexOfAny(new[] { '\r', '\n' });
        var line = index >= 0 ? text[..index] : text;
        return line.Length <= 120 ? line : line[..117] + "...";
    }

    private static PlannedNotification ForChange(AlertChange change, NotificationSettings settings, Func<Alert, string> deviceName)
    {
        var alert = change.Alert;
        var isProblem = change.IsProblem;
        var severitySettings = settings.ForSeverity(alert.Severity);

        // Recoveries and acknowledgements are informational: never sticky, never a sound.
        return new PlannedNotification(
            BuildTitle(change, deviceName(alert)),
            alert.DisplayRuleName,
            string.IsNullOrWhiteSpace(alert.Note) ? null : FirstLine(alert.Note!),
            alert.Severity,
            isProblem ? severitySettings.Persistence : ToastPersistence.Transient,
            isProblem && severitySettings.PlaySound,
            isProblem,
            change);
    }

    private static PlannedNotification Summary(
        IReadOnlyList<AlertChange> changes,
        NotificationSettings settings,
        Func<Alert, string> deviceName,
        Func<Alert, string?>? deviceLocation)
    {
        var problems = changes.Where(c => c.IsProblem).ToList();
        var critical = problems.Count(c => c.Alert.Severity == AlertSeverity.Critical);

        // "3 new critical alerts" / "core-sw-02 - High temperature" / "+2 more in Rack 3".
        var (title, body, detail) = problems.Count > 0
            ? AlertSummaryText.Build(problems
                .Select(c => new AlertSummaryItem(c.Alert.Severity, deviceName(c.Alert), c.Alert.DisplayRuleName, deviceLocation?.Invoke(c.Alert)))
                .ToList())
            : ($"{changes.Count} alert updates", "See DashyNMS for details.", null);

        var sticky = critical > 0 && settings.Critical.Persistence != ToastPersistence.Transient;

        return new PlannedNotification(
            title,
            body,
            detail,
            critical > 0 ? AlertSeverity.Critical : AlertSeverity.Warning,
            sticky ? ToastPersistence.UntilDismissed : ToastPersistence.Transient,
            PlaySound: true,
            IsProblem: problems.Count > 0,
            Change: null);
    }

    private static AlertNotificationPlan Suppressed(string reason)
        => new(Array.Empty<PlannedNotification>(), isSummary: false, notifiableCount: 0, reason);
}

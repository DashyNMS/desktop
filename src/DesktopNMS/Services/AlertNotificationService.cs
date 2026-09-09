using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DesktopNMS.Services;

/// <summary>Turns alert changes into Windows notifications.</summary>
public interface IAlertNotificationService
{
    /// <summary>Raised when the user clicks a toast or one of its buttons.</summary>
    event EventHandler<ToastActionRequest>? ActionRequested;

    /// <summary>Decides which of the changes deserve a toast, and shows them.</summary>
    void Handle(AlertPollResult result);

    /// <summary>Shows a sample toast so the user can see what a severity's settings look like.</summary>
    void ShowPreview(AlertSeverity severity);

    /// <summary>Removes DashyNMS toasts from the notification centre.</summary>
    void ClearHistory();
}

/// <summary>
/// Windows toast implementation.
/// </summary>
/// <remarks>
/// "Stickiness" maps onto toast scenarios: a Reminder or Alarm toast stays on
/// screen until the user acts on it, where a default toast fades after a few
/// seconds. Alarm additionally loops its sound, and Windows only honours it when
/// the toast has at least one button, which these always do.
/// </remarks>
public sealed class AlertNotificationService : IAlertNotificationService
{
    private const string ArgumentAction = "action";
    private const string ArgumentAlertId = "alertId";
    // Toast Group is capped at 16 characters on older Windows 10 builds.
    private const string ToastGroup = "DashyNMS";

    private static readonly Uri LoopingAlarmSound = new("ms-winsoundevent:Notification.Looping.Alarm");
    private static readonly Uri DefaultSound = new("ms-winsoundevent:Notification.Default");

    private readonly ISettingsStore _settings;
    private readonly ISessionService _session;
    private readonly IDeviceCache _devices;
    private readonly ILogger<AlertNotificationService> _logger;
    private readonly ITrayNotifier _trayFallback;

    private bool _toastsUnavailable;

    public AlertNotificationService(
        ISettingsStore settings,
        ISessionService session,
        IDeviceCache devices,
        ITrayNotifier trayFallback,
        ILogger<AlertNotificationService> logger)
    {
        _settings = settings;
        _session = session;
        _devices = devices;
        _trayFallback = trayFallback;
        _logger = logger;
    }

    /// <summary>Names the device the way the alert list does, honouring the hostname/sysName preference.</summary>
    private string DeviceNameFor(Alert alert)
        => _settings.Current.DeviceNameStyle.Resolve(_devices.Get(alert.DeviceId), alert.Hostname);

    public event EventHandler<ToastActionRequest>? ActionRequested;

    /// <summary>
    /// Hooks the COM activator. Called once at startup; activations arrive on a
    /// background thread, so the app marshals them to the UI itself.
    /// </summary>
    public void Initialise()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _logger.LogInformation("Windows toast notifications are available");
        }
        catch (Exception ex)
        {
            _toastsUnavailable = true;
            _logger.LogWarning(ex, "Windows toast notifications are unavailable; falling back to tray balloons");
        }
    }

    public void Handle(AlertPollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        var settings = _settings.Current.Notifications;

        if (result.Changes.Count == 0)
        {
            return;
        }

        if (!settings.Enabled)
        {
            _logger.LogInformation(
                "{Count} alert change(s) not notified: notifications are switched off in settings",
                result.Changes.Count);
            return;
        }

        if (result.IsFirstPoll && settings.SuppressOnFirstPoll)
        {
            _logger.LogInformation(
                "{Count} alert change(s) not notified: this was the first poll after starting",
                result.Changes.Count);
            return;
        }

        var notifiable = result.Changes.Where(change => ShouldNotify(change, settings)).ToList();

        if (notifiable.Count == 0)
        {
            // "Why did nothing pop up?" is the most common question this app
            // will be asked, so make the log answer it directly.
            _logger.LogInformation(
                "{Count} alert change(s), none notifiable ({Reasons})",
                result.Changes.Count,
                DescribeSuppression(result.Changes, settings));
            return;
        }

        _logger.LogInformation(
            "Notifying {Notifiable} of {Total} alert change(s)",
            notifiable.Count,
            result.Changes.Count);

        if (notifiable.Count > settings.MaxToastsPerPoll)
        {
            ShowSummary(notifiable);
            return;
        }

        foreach (var change in notifiable)
        {
            ShowChange(change, settings);
        }
    }

    /// <summary>Explains, in one line, why a set of changes produced no toast.</summary>
    private static string DescribeSuppression(IReadOnlyList<AlertChange> changes, NotificationSettings settings)
    {
        var reasons = new List<string>();

        var quiet = changes.Count(c => settings.IsInQuietHours(DateTime.Now, c.Alert.Severity));
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

    private bool ShouldNotify(AlertChange change, NotificationSettings settings)
    {
        var severity = change.Alert.Severity;

        if (settings.IsInQuietHours(DateTime.Now, severity))
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

    private void ShowChange(AlertChange change, NotificationSettings settings)
    {
        var alert = change.Alert;
        var isProblem = change.IsProblem;
        var severitySettings = settings.ForSeverity(alert.Severity);

        // Recoveries and acknowledgements are informational: never make them sticky.
        var persistence = isProblem ? severitySettings.Persistence : ToastPersistence.Transient;
        var playSound = isProblem ? severitySettings.PlaySound : false;

        var title = BuildTitle(change);
        var body = alert.DisplayRuleName;

        var detail = string.IsNullOrWhiteSpace(alert.Note)
            ? null
            : FirstLine(alert.Note!);

        try
        {
            if (_toastsUnavailable)
            {
                _trayFallback.ShowBalloon(title, body, alert.Severity);
                return;
            }

            var builder = new ToastContentBuilder()
                .AddArgument(ArgumentAction, "show")
                .AddArgument(ArgumentAlertId, alert.Id)
                .AddText(title)
                .AddText(body);

            if (detail is not null)
            {
                builder.AddText(detail);
            }

            builder.AddAttributionText(BuildAttribution(alert));

            ApplyAudio(builder, persistence, playSound);
            ApplyScenario(builder, persistence);
            AddButtons(builder, change, persistence);

            builder.Show(toast =>
            {
                toast.Tag = BuildTag(alert.Id, change.Kind);
                toast.Group = ToastGroup;

                // Keep problems in the notification centre for a day; clear
                // informational toasts out after an hour.
                toast.ExpirationTime = DateTimeOffset.Now.Add(isProblem ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1));
            });

            _logger.LogInformation(
                "Toast shown for alert {AlertId} ({Kind}, {Persistence}): {Title}",
                alert.Id,
                change.Kind,
                persistence,
                title);
        }
        catch (Exception ex)
        {
            _toastsUnavailable = true;
            _logger.LogWarning(ex, "Could not show a toast; falling back to tray balloons");
            _trayFallback.ShowBalloon(title, body, alert.Severity);
        }
    }

    private void ShowSummary(IReadOnlyList<AlertChange> changes)
    {
        var problems = changes.Where(c => c.IsProblem).ToList();
        var critical = problems.Count(c => c.Alert.Severity == AlertSeverity.Critical);
        var warning = problems.Count(c => c.Alert.Severity == AlertSeverity.Warning);

        var title = problems.Count > 0
            ? $"{problems.Count} new alerts"
            : $"{changes.Count} alert updates";

        var parts = new List<string>();
        if (critical > 0)
        {
            parts.Add($"{critical} critical");
        }

        if (warning > 0)
        {
            parts.Add($"{warning} warning");
        }

        var hosts = problems
            .Select(c => DeviceNameFor(c.Alert))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        var body = parts.Count > 0 ? string.Join(", ", parts) : "See DashyNMS for details.";
        var detail = hosts.Count > 0 ? string.Join(", ", hosts) : null;

        var makeSticky = critical > 0
            && _settings.Current.Notifications.Critical.Persistence != ToastPersistence.Transient;

        try
        {
            if (_toastsUnavailable)
            {
                _trayFallback.ShowBalloon(title, body, critical > 0 ? AlertSeverity.Critical : AlertSeverity.Warning);
                return;
            }

            var builder = new ToastContentBuilder()
                .AddArgument(ArgumentAction, "show")
                .AddText(title)
                .AddText(body);

            if (detail is not null)
            {
                builder.AddText(detail);
            }

            builder.AddAttributionText("DashyNMS");

            ApplyAudio(builder, makeSticky ? ToastPersistence.UntilDismissed : ToastPersistence.Transient, playSound: true);
            ApplyScenario(builder, makeSticky ? ToastPersistence.UntilDismissed : ToastPersistence.Transient);

            builder.AddButton(new ToastButton()
                .SetContent("Open DashyNMS")
                .AddArgument(ArgumentAction, "show")
                .SetBackgroundActivation());

            builder.AddButton(new ToastButtonDismiss("Dismiss"));

            builder.Show(toast =>
            {
                toast.Tag = "summary";
                toast.Group = ToastGroup;
                toast.ExpirationTime = DateTimeOffset.Now.AddDays(1);
            });
        }
        catch (Exception ex)
        {
            _toastsUnavailable = true;
            _logger.LogWarning(ex, "Could not show the summary toast");
            _trayFallback.ShowBalloon(title, body, critical > 0 ? AlertSeverity.Critical : AlertSeverity.Warning);
        }
    }

    public void ShowPreview(AlertSeverity severity)
    {
        var severitySettings = _settings.Current.Notifications.ForSeverity(severity);

        try
        {
            if (_toastsUnavailable)
            {
                _trayFallback.ShowBalloon(
                    $"{severity.ToDisplayString()} preview",
                    "This is what a DashyNMS alert looks like.",
                    severity);
                return;
            }

            var builder = new ToastContentBuilder()
                .AddArgument(ArgumentAction, "show")
                .AddText($"{severity.ToDisplayString()}: core-sw-01")
                .AddText("Preview: this is what a DashyNMS alert looks like.")
                .AddAttributionText("DashyNMS preview");

            ApplyAudio(builder, severitySettings.Persistence, severitySettings.PlaySound);
            ApplyScenario(builder, severitySettings.Persistence);

            builder.AddButton(new ToastButton()
                .SetContent("Open DashyNMS")
                .AddArgument(ArgumentAction, "show")
                .SetBackgroundActivation());

            builder.AddButton(new ToastButtonDismiss("Dismiss"));

            builder.Show(toast =>
            {
                toast.Tag = "preview";
                toast.Group = ToastGroup;
                toast.ExpirationTime = DateTimeOffset.Now.AddMinutes(10);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the preview toast");
            _trayFallback.ShowBalloon(
                $"{severity.ToDisplayString()} preview",
                "Windows notifications are not available; showing a tray balloon instead.",
                severity);
        }
    }

    public void ClearHistory()
    {
        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clear the toast history");
        }
    }

    private void AddButtons(ToastContentBuilder builder, AlertChange change, ToastPersistence persistence)
    {
        var alert = change.Alert;

        if (change.IsProblem)
        {
            builder.AddButton(new ToastButton()
                .SetContent("Acknowledge")
                .AddArgument(ArgumentAction, "acknowledge")
                .AddArgument(ArgumentAlertId, alert.Id)
                .SetBackgroundActivation());
        }

        var connection = _session.Connection;
        if (connection is not null)
        {
            builder.AddButton(new ToastButton()
                .SetContent("Open in LibreNMS")
                .SetProtocolActivation(connection.AlertUrl(alert.Id)));
        }

        // A sticky toast with no way to clear it is an irritation, and Alarm
        // scenario requires a button to be honoured at all.
        if (persistence != ToastPersistence.Transient)
        {
            builder.AddButton(new ToastButtonDismiss("Dismiss"));
        }
    }

    private static void ApplyScenario(ToastContentBuilder builder, ToastPersistence persistence)
    {
        switch (persistence)
        {
            case ToastPersistence.UntilDismissed:
                builder.SetToastScenario(ToastScenario.Reminder);
                break;
            case ToastPersistence.UntilDismissedWithAlarm:
                builder.SetToastScenario(ToastScenario.Alarm);
                break;
        }
    }

    private static void ApplyAudio(ToastContentBuilder builder, ToastPersistence persistence, bool playSound)
    {
        if (!playSound)
        {
            builder.AddAudio(new ToastAudio { Silent = true });
            return;
        }

        if (persistence == ToastPersistence.UntilDismissedWithAlarm)
        {
            builder.AddAudio(LoopingAlarmSound, loop: true);
            return;
        }

        builder.AddAudio(DefaultSound);
    }

    private string BuildTitle(AlertChange change)
    {
        var alert = change.Alert;
        var device = DeviceNameFor(alert);

        return change.Kind switch
        {
            AlertChangeKind.Recovered => $"Recovered: {device}",
            AlertChangeKind.Acknowledged => $"Acknowledged: {device}",
            AlertChangeKind.Unacknowledged => $"Unacknowledged: {device}",
            AlertChangeKind.Reopened => $"{alert.Severity.ToDisplayString()} again: {device}",
            _ => $"{alert.Severity.ToDisplayString()}: {device}",
        };
    }

    private string BuildAttribution(Alert alert)
    {
        var host = _session.Connection?.WebRoot.Host ?? "LibreNMS";
        var time = alert.Timestamp?.ToString("HH:mm", CultureInfo.CurrentCulture);

        return time is null ? host : $"{host} at {time}";
    }

    private static string BuildTag(int alertId, AlertChangeKind kind)
    {
        // Toast tags are limited to 64 characters.
        var tag = $"alert-{alertId}-{kind}";
        return tag.Length <= 64 ? tag : tag[..64];
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(new[] { '\r', '\n' });
        var line = index >= 0 ? text[..index] : text;
        return line.Length <= 120 ? line : line[..117] + "...";
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        try
        {
            var arguments = ToastArguments.Parse(args.Argument);

            var action = arguments.Contains(ArgumentAction) ? arguments[ArgumentAction] : "show";
            int? alertId = arguments.Contains(ArgumentAlertId)
                           && int.TryParse(arguments[ArgumentAlertId], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

            var request = action.Equals("acknowledge", StringComparison.OrdinalIgnoreCase)
                ? new ToastActionRequest(ToastAction.Acknowledge, alertId)
                : new ToastActionRequest(ToastAction.Show, alertId);

            ActionRequested?.Invoke(this, request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not handle a toast activation ({Argument})", args.Argument);
        }
    }
}

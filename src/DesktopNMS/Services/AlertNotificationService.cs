using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopNMS.Core;
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
    private readonly ISelfActionTracker _selfActions;
    private readonly ILogger<AlertNotificationService> _logger;
    private readonly ITrayNotifier _trayFallback;

    private bool _toastsUnavailable;

    public AlertNotificationService(
        ISettingsStore settings,
        ISessionService session,
        IDeviceCache devices,
        ISelfActionTracker selfActions,
        ITrayNotifier trayFallback,
        ILogger<AlertNotificationService> logger)
    {
        _settings = settings;
        _session = session;
        _devices = devices;
        _selfActions = selfActions;
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
        if (!result.Succeeded || result.Changes.Count == 0)
        {
            return;
        }

        // Before deciding what to show - and regardless of whether anything
        // is shown - clear out problem toasts this poll has made stale.
        RemoveStaleProblemToasts(result.Changes);

        // Which changes notify, and what they say, is decided in Core so the
        // phone app decides the same way (#195); this class only shows it.
        var plan = AlertNotificationPlanner.Plan(
            result.Changes,
            result.IsFirstPoll,
            _settings.Current.Notifications,
            DateTime.Now,
            DeviceNameFor,
            alert => _devices.Get(alert.DeviceId)?.Location,
            change => _selfActions.WasSelfInitiated(change.Alert.Id, change.Kind));

        if (plan.Notifications.Count == 0)
        {
            // "Why did nothing pop up?" is the most common question this app
            // will be asked, so make the log answer it directly.
            _logger.LogInformation(
                "{Count} alert change(s) not notified: {Reason}",
                result.Changes.Count,
                plan.SuppressionReason);
            return;
        }

        _logger.LogInformation(
            "Notifying {Notifiable} of {Total} alert change(s){Summary}",
            plan.NotifiableCount,
            result.Changes.Count,
            plan.IsSummary ? " as one summary" : string.Empty);

        foreach (var notification in plan.Notifications)
        {
            if (notification.Change is { } change)
            {
                ShowChange(change, notification);
            }
            else
            {
                ShowSummary(notification);
            }
        }
    }

    private void ShowChange(AlertChange change, PlannedNotification notification)
    {
        var alert = change.Alert;

        try
        {
            if (_toastsUnavailable)
            {
                _trayFallback.ShowBalloon(notification.Title, notification.Body, alert.Severity);
                return;
            }

            var builder = new ToastContentBuilder()
                .AddArgument(ArgumentAction, "show")
                .AddArgument(ArgumentAlertId, alert.Id)
                .AddText(notification.Title)
                .AddText(notification.Body);

            if (notification.Detail is not null)
            {
                builder.AddText(notification.Detail);
            }

            builder.AddAttributionText(BuildAttribution(alert));

            ApplyAudio(builder, notification.Persistence, notification.PlaySound);
            ApplyScenario(builder, notification.Persistence);
            AddButtons(builder, change, notification.Persistence);

            builder.Show(toast =>
            {
                toast.Tag = BuildTag(alert.Id, change.Kind);
                toast.Group = ToastGroup;

                // Keep problems in the notification centre for a day; clear
                // informational toasts out after an hour.
                toast.ExpirationTime = DateTimeOffset.Now.Add(notification.IsProblem ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1));
            });

            _logger.LogInformation(
                "Toast shown for alert {AlertId} ({Kind}, {Persistence}): {Title}",
                alert.Id,
                change.Kind,
                notification.Persistence,
                notification.Title);
        }
        catch (Exception ex)
        {
            _toastsUnavailable = true;
            _logger.LogWarning(ex, "Could not show a toast; falling back to tray balloons");
            _trayFallback.ShowBalloon(notification.Title, notification.Body, alert.Severity);
        }
    }

    private void ShowSummary(PlannedNotification notification)
    {
        try
        {
            if (_toastsUnavailable)
            {
                _trayFallback.ShowBalloon(notification.Title, notification.Body, notification.Severity);
                return;
            }

            var builder = new ToastContentBuilder()
                .AddArgument(ArgumentAction, "show")
                .AddText(notification.Title)
                .AddText(notification.Body);

            if (notification.Detail is not null)
            {
                builder.AddText(notification.Detail);
            }

            builder.AddAttributionText("DashyNMS");

            ApplyAudio(builder, notification.Persistence, notification.PlaySound);
            ApplyScenario(builder, notification.Persistence);

            // No "Open" button - clicking the toast body already does this,
            // via the "show" argument set on the whole toast above.
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
            _trayFallback.ShowBalloon(notification.Title, notification.Body, notification.Severity);
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

            // No "Open" button - clicking the toast body already does this,
            // via the "show" argument set on the whole toast above.
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

    /// <summary>
    /// Toast kinds that ask for attention about a problem - the ones worth
    /// removing once that problem is dealt with. Every toast is tagged per
    /// alert id and kind (see <see cref="BuildTag"/>), so removing one that
    /// was never shown is a harmless no-op.
    /// </summary>
    private static readonly AlertChangeKind[] ProblemKinds =
    {
        AlertChangeKind.New,
        AlertChangeKind.Reopened,
        AlertChangeKind.Unacknowledged,
    };

    /// <summary>
    /// Issue #160: a sticky Critical toast that fired while you were away
    /// would otherwise still be sitting on screen (or in the notification
    /// centre) after its alert recovered - demanding attention for a problem
    /// that's already fixed. So when an alert recovers, or is acknowledged
    /// (from this app, its own toast button, or the website - all arrive as
    /// the same change on the next poll, and acknowledging already means
    /// "I've seen this"), its earlier problem toasts are removed. Runs
    /// whether or not the recovery/acknowledgement itself is notified.
    /// </summary>
    private void RemoveStaleProblemToasts(IReadOnlyList<AlertChange> changes)
    {
        if (_toastsUnavailable)
        {
            return;
        }

        foreach (var change in changes)
        {
            if (change.Kind is not (AlertChangeKind.Recovered or AlertChangeKind.Acknowledged))
            {
                continue;
            }

            foreach (var kind in ProblemKinds)
            {
                try
                {
                    ToastNotificationManagerCompat.History.Remove(BuildTag(change.Alert.Id, kind), ToastGroup);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not remove the {Kind} toast for alert {AlertId}", kind, change.Alert.Id);
                }
            }
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

    /// <summary>
    /// Acknowledge (for a problem) and Dismiss (for a sticky toast) - no
    /// "Open" button, since clicking the toast body itself already does that
    /// (see the "show" argument <see cref="ShowChange"/> sets on the whole
    /// toast), and no website link either, so a click never leaves the app
    /// for the browser.
    /// </summary>
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

    private string BuildAttribution(Alert alert)
    {
        var host = _session.Connection?.WebRoot.Host ?? "LibreNMS";
        var time = ServerTime.ToLocal(alert.Timestamp, _settings.Current.ServerTimestampsAreUtc)?.ToString("HH:mm", CultureInfo.CurrentCulture);

        return time is null ? host : $"{host} at {time}";
    }

    private static string BuildTag(int alertId, AlertChangeKind kind)
    {
        // Toast tags are limited to 64 characters.
        var tag = $"alert-{alertId}-{kind}";
        return tag.Length <= 64 ? tag : tag[..64];
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

            var request = action.ToLowerInvariant() switch
            {
                "acknowledge" => new ToastActionRequest(ToastAction.Acknowledge, alertId),
                "install-update" => new ToastActionRequest(ToastAction.InstallUpdate, null),
                _ => new ToastActionRequest(ToastAction.Show, alertId),
            };

            ActionRequested?.Invoke(this, request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not handle a toast activation ({Argument})", args.Argument);
        }
    }
}

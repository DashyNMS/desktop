using DesktopNMS.Core;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>The platform-independent content and identity of a native notification.</summary>
public sealed record NativeNotification(string Id, string Title, string Body, string? Alert,
    bool CanAcknowledge, AlertSeverity Severity, bool Persistent, bool PlaySound, bool IsUpdate);

public interface INativeNotificationBackend : IDisposable
{
    event EventHandler<ToastActionRequest>? ActionRequested;
    Task ShowAsync(NativeNotification notification);
    Task CloseAsync(string id);
}

/// <summary>Uses Core's group notification policy for macOS and Linux; OS delivery stays behind a backend.</summary>
public sealed class NativeAlertNotifications : IAlertNotificationService, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly ISelfActionTracker _selfActions;
    private readonly ApiPermissions _permissions;
    private readonly INativeNotificationBackend _backend;
    private readonly ILogger _logger;
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;
    private bool _disposed;

    public NativeAlertNotifications(ISettingsStore settings, IDeviceCache devices, ISelfActionTracker selfActions,
        ILibreNmsClient client, INativeNotificationBackend backend, ILogger<NativeAlertNotifications> logger)
    {
        _settings = settings;
        _devices = devices;
        _selfActions = selfActions;
        _permissions = client.Permissions;
        _backend = backend;
        _logger = logger;
        _backend.ActionRequested += OnAction;
    }

    public event EventHandler<ToastActionRequest>? ActionRequested;
    private void OnAction(object? sender, ToastActionRequest action) => ActionRequested?.Invoke(this, action);
    internal Task WhenIdle { get { lock (_gate) return _pending; } }

    public void Handle(AlertPollResult result)
    {
        if (!result.Succeeded) return;
        if (result.IsBaseline)
        {
            Enqueue(async () =>
            {
                foreach (var id in _shown.Where(id => id.StartsWith("group-", StringComparison.Ordinal) || id == "summary").ToArray())
                    await CloseAsync(id).ConfigureAwait(false);
            });
            return;
        }

        var plan = AlertNotificationPlanner.Plan(result.Changes, result.IsFirstPoll, _settings.Current.Notifications, DateTime.Now,
            alert => _settings.Current.DeviceNameStyle.Resolve(_devices.Get(alert.DeviceId), alert.Hostname),
            alert => _devices.Get(alert.DeviceId)?.Location,
            change => _selfActions.WasSelfInitiated(change.Alert.Id, change.Kind));

        Enqueue(async () =>
        {
            // Recovery/acknowledgement withdraws a stale problem even if that
            // change is itself suppressed by the user's notification choices.
            foreach (var change in result.Changes.Where(c => c.Kind is AlertChangeKind.Recovered or AlertChangeKind.Acknowledged))
                await CloseAsync("group-" + change.NotificationTag).ConfigureAwait(false);

            foreach (var planned in plan.Notifications)
            {
                var change = planned.Change;
                var id = change is null ? "summary" : "group-" + change.NotificationTag;
                var body = planned.Detail is null ? planned.Body : planned.Body + "\n" + planned.Detail;
                await _backend.ShowAsync(new(id, planned.Title, body, change?.Alert.Identity.ToString(),
                    change is not null && (change.IsProblem || change.Kind == AlertChangeKind.Updated && change.Alert.State != AlertState.Acknowledged)
                        && !_permissions.IsAcknowledgeRefused(change.Alert.Identity),
                    planned.Severity, planned.Persistence != ToastPersistence.Transient, planned.PlaySound, planned.IsUpdate)).ConfigureAwait(false);
                _shown.Add(id);
            }
        });
    }

    public void ShowPreview(AlertSeverity severity)
    {
        var options = _settings.Current.Notifications.ForSeverity(severity);
        Enqueue(async () =>
        {
            await _backend.ShowAsync(new("preview", $"{severity.ToDisplayString()}: core-sw-01", "Preview: this is what a DashyNMS alert looks like.",
                null, false, severity, options.Persistence != ToastPersistence.Transient, options.PlaySound, false)).ConfigureAwait(false);
            _shown.Add("preview");
        });
    }

    public void ClearHistory() => Enqueue(async () =>
    {
        foreach (var id in _shown.ToArray()) await CloseAsync(id).ConfigureAwait(false);
    });

    private async Task CloseAsync(string id)
    {
        if (!_shown.Remove(id)) return;
        await _backend.CloseAsync(id).ConfigureAwait(false);
    }

    private void Enqueue(Func<Task> operation)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = _pending.ContinueWith(async _ =>
            {
                try { await operation().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "A native notification couldn't be delivered"); }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _backend.ActionRequested -= OnAction;
        // Finish any already queued writes before closing a helper's input.
        _ = WhenIdle.ContinueWith(_ => _backend.Dispose(), TaskScheduler.Default);
    }
}

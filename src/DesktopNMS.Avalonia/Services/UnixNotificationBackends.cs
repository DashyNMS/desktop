using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopNMS.Core.Models;
using DesktopNMS.Security;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>libnotify talks to the desktop's freedesktop notification service and waits for actions.</summary>
public sealed class LinuxNotificationBackend(ILogger<LinuxNotificationBackend> logger) : INativeNotificationBackend
{
    private sealed record Delivery(uint OsId, Process Process, NativeNotification Notification);
    private readonly ConcurrentDictionary<string, Delivery> _deliveries = new(StringComparer.Ordinal);
    public event EventHandler<ToastActionRequest>? ActionRequested;

    public async Task ShowAsync(NativeNotification notification)
    {
        _deliveries.TryGetValue(notification.Id, out var previous);
        var start = new ProcessStartInfo("notify-send")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--app-name=DashyNMS", "--icon=net.dashynms.desktop", "--print-id", "--wait",
                     "--hint=string:desktop-entry:net.dashynms.desktop", "--action=default=Open DashyNMS",
                     "--urgency=" + (notification.Severity == AlertSeverity.Critical ? "critical" : "normal"),
                     "--expire-time=" + (notification.Persistent ? "0" : "10000"),
                     "--hint=boolean:suppress-sound:" + (!notification.PlaySound || notification.IsUpdate ? "true" : "false") })
            start.ArgumentList.Add(argument);
        if (previous is not null) start.ArgumentList.Add("--replace-id=" + previous.OsId.ToString(CultureInfo.InvariantCulture));
        if (notification.CanAcknowledge) start.ArgumentList.Add("--action=acknowledge=Acknowledge");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(notification.Title);
        start.ArgumentList.Add(WebUtility.HtmlEncode(notification.Body));
        var process = Process.Start(start) ?? throw new InvalidOperationException("Install libnotify-bin to enable native notifications.");
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!uint.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var osId) || osId == 0)
                throw new InvalidOperationException("The desktop didn't accept the notification. Check that a notification service and libnotify-bin are installed.");
            var delivery = new Delivery(osId, process, notification);
            _deliveries[notification.Id] = delivery;
            if (previous is not null) Stop(previous.Process);
            _ = ReadActionAsync(delivery, errors);
        }
        catch { Stop(process); process.Dispose(); throw; }
    }

    private async Task ReadActionAsync(Delivery delivery, Task<string> errors)
    {
        try
        {
            var action = await delivery.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            if (_deliveries.TryGetValue(delivery.Notification.Id, out var current) && ReferenceEquals(current, delivery)
                && action is "default" or "acknowledge")
            {
                AlertKey? alert = AlertKey.TryParse(delivery.Notification.Alert, out var key) ? key : null;
                var request = action == "acknowledge" && delivery.Notification.CanAcknowledge ? ToastAction.Acknowledge
                    : delivery.Notification.DefaultAction switch { "install-update" => ToastAction.InstallUpdate, "updates" => ToastAction.OpenUpdates, _ => ToastAction.Show };
                ActionRequested?.Invoke(this, new(request, alert));
            }
            await delivery.Process.WaitForExitAsync().ConfigureAwait(false);
            var error = await errors.ConfigureAwait(false);
            if (delivery.Process.ExitCode != 0 && error.Length > 0) logger.LogWarning("Linux notifications: {Reason}", error.Trim());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Notification action listener ended");
        }
        finally { delivery.Process.Dispose(); }
    }

    public Task CloseAsync(string id)
    {
        if (_deliveries.TryRemove(id, out var delivery))
        {
            var result = new ProcessCommandRunner().Run("gdbus", ["call", "--session", "--dest", "org.freedesktop.Notifications",
                "--object-path", "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.CloseNotification",
                delivery.OsId.ToString(CultureInfo.InvariantCulture)]);
            Stop(delivery.Process);
            if (result.ExitCode != 0) logger.LogDebug("Could not withdraw a Linux notification: {Reason}", result.Describe());
        }
        return Task.CompletedTask;
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        foreach (var delivery in _deliveries.Values) Stop(delivery.Process);
        _deliveries.Clear();
    }
}

/// <summary>UNUserNotificationCenter runs inside the app process, keeping its delegate alive for actions.</summary>
public sealed class MacNotificationBackend : INativeNotificationBackend
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeCallback(IntPtr json);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeInitialise(NativeCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Send([MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Close([MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    private readonly ILogger _logger;
    private readonly NativeCallback _callback;
    private static readonly List<NativeCallback> CallbackRoots = new();
    private IntPtr _library;
    private Send? _send;
    private Close? _close;
    public event EventHandler<ToastActionRequest>? ActionRequested;

    public MacNotificationBackend(ILogger<MacNotificationBackend> logger)
    {
        _logger = logger;
        _callback = OnCallback;
    }

    private void EnsureLoaded()
    {
        if (_library != IntPtr.Zero) return;
        _library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libdashynms-notifications.dylib"));
        lock (CallbackRoots) CallbackRoots.Add(_callback);
        _send = Marshal.GetDelegateForFunctionPointer<Send>(NativeLibrary.GetExport(_library, "dashynms_notification_send"));
        _close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(_library, "dashynms_notification_close"));
        Marshal.GetDelegateForFunctionPointer<NativeInitialise>(NativeLibrary.GetExport(_library, "dashynms_notifications_initialize"))(_callback);
    }

    public void Initialise() => EnsureLoaded();

    private void OnCallback(IntPtr json)
    {
        try
        {
            using var data = JsonDocument.Parse(Marshal.PtrToStringUTF8(json) ?? "{}");
            if (data.RootElement.TryGetProperty("error", out var error)) { _logger.LogWarning("macOS notifications: {Reason}", error.GetString()); return; }
            var action = data.RootElement.GetProperty("action").GetString();
            var value = data.RootElement.TryGetProperty("alert", out var alert) ? alert.GetString() : null;
            AlertKey? key = AlertKey.TryParse(value, out var parsed) ? parsed : null;
            var request = action switch { "acknowledge" => ToastAction.Acknowledge, "install-update" => ToastAction.InstallUpdate, "updates" => ToastAction.OpenUpdates, _ => ToastAction.Show };
            ActionRequested?.Invoke(this, new(request, key));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not handle a macOS notification action"); }
    }

    public Task ShowAsync(NativeNotification notification)
    {
        EnsureLoaded();
        _send!(JsonSerializer.Serialize(notification));
        return Task.CompletedTask;
    }

    public Task CloseAsync(string id) { _close?.Invoke(id); return Task.CompletedTask; }

    public void Dispose()
    {
        // The Objective-C notification centre retains its delegate until the
        // process exits. Keep both its Swift code and managed callback alive.
        GC.KeepAlive(_callback);
    }
}

using DesktopNMS.Core.Models;
#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
#endif

namespace DesktopNMS.Services;

public sealed class PlatformUpdateAnnouncements : IUpdateAnnouncements
{
#if WINDOWS
    private readonly ITrayNotifier _fallback;
    public PlatformUpdateAnnouncements(ITrayNotifier fallback) => _fallback = fallback;
    public Task ShowAsync(string title, string body, bool ready)
    {
        try
        {
            var toast = new ToastContentBuilder().AddArgument("action", ready ? "install-update" : "updates").AddText(title).AddText(body);
            if (ready) toast.AddButton(new ToastButton().SetContent("Restart to update").AddArgument("action", "install-update"));
            toast.AddButton(new ToastButtonDismiss("Dismiss"));
            toast.Show(value => { value.Tag = "app-update"; value.Group = "DashyNMS"; value.ExpirationTime = DateTimeOffset.Now.AddDays(3); });
        }
        catch { _fallback.ShowBalloon(title, body, AlertSeverity.Ok); }
        return Task.CompletedTask;
    }
#else
    private readonly INativeNotificationBackend _backend;
    public PlatformUpdateAnnouncements(INativeNotificationBackend backend) => _backend = backend;
    public Task ShowAsync(string title, string body, bool ready) => _backend.ShowAsync(new NativeNotification(
        "app-update", title, body, null, false, AlertSeverity.Ok, false, false, true, ready ? "install-update" : "updates"));
#endif
}

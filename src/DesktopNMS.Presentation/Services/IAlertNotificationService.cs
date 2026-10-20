using DesktopNMS.Core.Models;

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

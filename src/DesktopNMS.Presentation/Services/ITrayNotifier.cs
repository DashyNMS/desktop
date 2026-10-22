using DesktopNMS.Core.Models;

namespace DesktopNMS.Services;

/// <summary>The Windows balloon fallback when native toasts are unavailable.</summary>
public interface ITrayNotifier
{
    void ShowBalloon(string title, string message, AlertSeverity severity);
}

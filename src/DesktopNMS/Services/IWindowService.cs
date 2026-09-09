using System;

namespace DesktopNMS.Services;

/// <summary>
/// Window and dialog operations a view model needs, kept behind an interface so
/// view models never touch Window types directly.
/// </summary>
public interface IWindowService
{
    /// <summary>Shows the main window, restoring and activating it if it is already open.</summary>
    void ShowMain();

    /// <summary>Hides the main window to the notification area.</summary>
    void HideMain();

    /// <summary>Shows the main window with the Devices tab selected.</summary>
    void ShowDevicesTab();

    /// <summary>
    /// Shows the main window with the Alerts tab selected, filtered to the
    /// given device. Used by the device view's "Show alerts" action, routed
    /// through here rather than a direct reference so the alerts and device
    /// view models do not depend on each other.
    /// </summary>
    void ShowAlertsForDevice(string deviceSearchTerm);

    /// <summary>Shows the settings dialog. Returns true if the user saved.</summary>
    bool ShowSettingsDialog();

    /// <summary>Shows the sign-in dialog. Returns true if a session was established.</summary>
    bool ShowSignInDialog();

    /// <summary>Opens a URL in the default browser.</summary>
    void OpenUrl(Uri url);

    void ShowError(string title, string message);

    void ShowInformation(string title, string message);

    bool Confirm(string title, string message);

    /// <summary>Shuts the application down, including the tray icon.</summary>
    void Exit();
}

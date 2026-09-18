using System;
using System.Collections.Generic;
using DesktopNMS.Core.Models;

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

    /// <summary>
    /// Shows a device's detail window, non-modal. Reuses and activates the
    /// existing window if this device's is already open, rather than opening
    /// a second one.
    /// </summary>
    void ShowDeviceDetail(int deviceId);

    /// <summary>Closes a device's detail window if it is currently open - a no-op otherwise.</summary>
    void CloseDeviceDetail(int deviceId);

    /// <summary>
    /// Shows the main window with the Devices tab selected and every filter
    /// reset except Location, which is isolated down to just this one value -
    /// used by the device view's Location link, routed through here rather
    /// than a direct reference so the device detail and device list view
    /// models do not depend on each other.
    /// </summary>
    void ShowDevicesFilteredByLocation(string location);

    /// <summary>Same as <see cref="ShowDevicesFilteredByLocation"/>, but isolating the Group facet instead - used by the device view's Device Groups section.</summary>
    void ShowDevicesFilteredByGroup(string groupName);

    /// <summary>Shows the settings dialog. Returns true if the user saved.</summary>
    bool ShowSettingsDialog();

    /// <summary>
    /// Shows the Devices tab's Type/Location/Group filter dialog, modal to
    /// the main window. Every checkbox inside it filters the device list
    /// live, so unlike the other dialogs here there is nothing to report
    /// back once it closes.
    /// </summary>
    void ShowDeviceFiltersDialog();

    /// <summary>Shows the sign-in dialog. Returns true if a session was established.</summary>
    bool ShowSignInDialog();

    /// <summary>Shows the "Add device" dialog. Returns true if a device was added.</summary>
    bool ShowAddDeviceDialog();

    /// <summary>Shows the "Add device group" dialog. Returns true if a group was created.</summary>
    bool ShowAddDeviceGroupDialog();

    /// <summary>Shows the "Edit device group" dialog for an existing static group. Returns true if it was saved.</summary>
    bool ShowEditDeviceGroupDialog(DeviceGroup group);

    /// <summary>Shows the "Add to group" dialog for a Devices-grid multi-selection (issue #39). Returns true if the devices were added.</summary>
    bool ShowAddDevicesToGroupDialog(IReadOnlyList<int> deviceIds);

    /// <summary>Shows the "Add location" dialog. Returns true if a location was created.</summary>
    bool ShowAddLocationDialog();

    /// <summary>Shows the "Edit location" dialog for an existing location. Returns true if it was saved.</summary>
    bool ShowEditLocationDialog(Location location);

    /// <summary>Shows the "Add rule" dialog. Returns true if a rule was created.</summary>
    bool ShowAddRuleDialog();

    /// <summary>Shows the "Edit rule" dialog for an existing alert rule. Returns true if it was saved.</summary>
    bool ShowEditRuleDialog(AlertRule rule);

    /// <summary>Shows the "Add alert template" dialog. Returns true if a template was created.</summary>
    bool ShowAddAlertTemplateDialog();

    /// <summary>Shows the "Edit alert template" dialog for an existing template. Returns true if it was saved.</summary>
    bool ShowEditAlertTemplateDialog(AlertTemplate template);

    /// <summary>Opens a URL in the default browser.</summary>
    void OpenUrl(Uri url);

    /// <summary>
    /// "Open in" on Device Details: hands a web/telnet/ssh URI to whatever the
    /// OS has registered for its scheme (a browser, PuTTY, the built-in
    /// Telnet client if enabled, ...). Unlike <see cref="OpenUrl"/>, the URI
    /// here is one DashyNMS built itself from the device's own address, not a
    /// link handed over as-is from the LibreNMS server.
    /// </summary>
    void OpenExternalTool(Uri uri);

    void ShowError(string title, string message);

    void ShowInformation(string title, string message);

    bool Confirm(string title, string message);

    /// <summary>
    /// Same as <see cref="Confirm"/>, but with a "don't ask me again"
    /// checkbox. Its state is returned separately from the confirm/cancel
    /// answer and regardless of it - the checkbox is a standalone "stop
    /// asking me" declaration, so callers should honour it even when the
    /// user cancels this particular prompt.
    /// </summary>
    (bool Confirmed, bool DontAskAgain) ConfirmWithOptOut(string title, string message, string dontAskAgainLabel = "Don't ask me again");

    /// <summary>Shuts the application down, including the tray icon.</summary>
    void Exit();
}

using System;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Services;

/// <summary>What the user clicked on a toast.</summary>
public enum ToastAction
{
    /// <summary>The toast body was clicked: bring the window up and select the alert.</summary>
    Show,

    /// <summary>The "Acknowledge" button was clicked.</summary>
    Acknowledge,

    /// <summary>"Restart to update" on the update-ready toast.</summary>
    InstallUpdate,

    /// <summary>Open Settings, About for release and installation options.</summary>
    OpenUpdates,
}

/// <summary>A toast activation, marshalled onto the UI thread by the app.</summary>
public sealed class ToastActionRequest : EventArgs
{
    public ToastActionRequest(ToastAction action, AlertKey? alert)
    {
        Action = action;
        Alert = alert;
    }

    public ToastAction Action { get; }

    /// <summary>
    /// The alert or fault the toast was about - "fault:12", never a bare id,
    /// so a toast from before LibreNMS 26.10 can't act on an unrelated fault
    /// (DashyNMS/core#5). Null for toasts that are not about a specific alert.
    /// </summary>
    public AlertKey? Alert { get; }
}

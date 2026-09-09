using System;

namespace DesktopNMS.Services;

/// <summary>What the user clicked on a toast.</summary>
public enum ToastAction
{
    /// <summary>The toast body was clicked: bring the window up and select the alert.</summary>
    Show,

    /// <summary>The "Acknowledge" button was clicked.</summary>
    Acknowledge,
}

/// <summary>A toast activation, marshalled onto the UI thread by the app.</summary>
public sealed class ToastActionRequest : EventArgs
{
    public ToastActionRequest(ToastAction action, int? alertId)
    {
        Action = action;
        AlertId = alertId;
    }

    public ToastAction Action { get; }

    /// <summary>Null for toasts that are not about a specific alert.</summary>
    public int? AlertId { get; }
}

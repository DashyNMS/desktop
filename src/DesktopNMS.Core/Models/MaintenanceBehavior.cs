namespace DesktopNMS.Core.Models;

/// <summary>
/// How LibreNMS treats alerts for a device while it's under a scheduled
/// maintenance window - the same three options as the "Alert Behavior"
/// dropdown on LibreNMS's own "Schedule Downtime" form
/// (<c>LibreNMS\Enum\MaintenanceBehavior</c>). Sent as the write request's
/// <c>behavior</c> field; omitting it lets the server fall back to its
/// configured default (<c>alert.scheduled_maintenance_default_behavior</c>,
/// which ships set to <see cref="SkipAlerts"/>).
/// </summary>
public enum MaintenanceBehavior
{
    /// <summary>Existing alerts are neither raised nor recovered while the window is active - LibreNMS's long-standing default behaviour.</summary>
    SkipAlerts = 1,

    /// <summary>Alerts still evaluate and are recorded, but no notifications are sent.</summary>
    MuteAlerts = 2,

    /// <summary>The maintenance window has no effect on alerting at all.</summary>
    RunAlerts = 3,
}

public static class MaintenanceBehaviorExtensions
{
    public static string ToDisplayString(this MaintenanceBehavior behavior) => behavior switch
    {
        MaintenanceBehavior.SkipAlerts => "Skip alerts",
        MaintenanceBehavior.MuteAlerts => "Mute alerts",
        MaintenanceBehavior.RunAlerts => "Run alerts as normal",
        _ => behavior.ToString(),
    };
}

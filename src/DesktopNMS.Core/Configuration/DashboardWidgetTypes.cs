namespace DesktopNMS.Core.Configuration;

/// <summary>
/// The <see cref="DashboardWidget.WidgetType"/> values the desktop app knows
/// how to show. A dashboard can hold any number of widgets of one type, each
/// with its own <see cref="DashboardWidget.Id"/>, title and settings. A type
/// not listed here - one DashyNMS Mobile or a newer version added - is shown
/// as "not supported in this version" and kept untouched (#196).
/// </summary>
public static class DashboardWidgetTypes
{
    public const string Sensors = "Sensors";
    public const string Alerts = "Alerts";
    public const string AlertsGauge = "AlertsGauge";
    public const string DeviceStatus = "DeviceStatus";
    public const string RecentlyViewed = "RecentlyViewed";
    public const string PinnedDevices = "PinnedDevices";
    public const string Graph = "Graph";
    public const string Wireless = "Wireless";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Sensors, Alerts, AlertsGauge, DeviceStatus, RecentlyViewed, PinnedDevices, Graph, Wireless,
    };

    public static bool IsKnown(string? widgetType) => widgetType is not null && Known.Contains(widgetType);
}

namespace DesktopNMS.Core.Configuration;

/// <summary>
/// The welcome card's "Use the starter dashboard" (#233): a ready-made set
/// of widgets that covers the basics, laid out on the grid in one go - the
/// state of the fleet across the top, the busiest ports and the devices you
/// looked at last underneath.
/// </summary>
public static class StarterDashboard
{
    /// <summary>New widgets (fresh ids) with their positions, in reading order.</summary>
    public static IReadOnlyList<DashboardWidget> Create() => new[]
    {
        Widget(DashboardWidgetTypes.AlertsGauge, "Alerts gauge", column: 0, row: 0, columnSpan: 10, rowSpan: 7),
        Widget(DashboardWidgetTypes.DeviceStatus, "Device status", column: 10, row: 0, columnSpan: 10, rowSpan: 7),
        Widget(DashboardWidgetTypes.Alerts, "Alerts", column: 20, row: 0, columnSpan: 12, rowSpan: 14),
        Widget(DashboardWidgetTypes.TopInterfaces, "Top interfaces", column: 0, row: 7, columnSpan: 12, rowSpan: 7),
        Widget(DashboardWidgetTypes.RecentlyViewed, "Recently viewed", column: 12, row: 7, columnSpan: 8, rowSpan: 7),
    };

    private static DashboardWidget Widget(string type, string title, int column, int row, int columnSpan, int rowSpan) => new()
    {
        WidgetType = type,
        Title = title,
        Column = column,
        Row = row,
        ColumnSpan = columnSpan,
        RowSpan = rowSpan,
    };
}

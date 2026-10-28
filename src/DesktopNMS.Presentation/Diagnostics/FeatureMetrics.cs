using DesktopNMS.Core.Configuration;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Diagnostics;

/// <summary>Only broad built-in actions reach the locally aggregated counter.</summary>
public sealed class FeatureMetrics(IAppMetrics? metrics)
{
    public void Navigated(MainTab previous, MainTab current)
    {
        if (previous == current) return;
        var area = current switch
        {
            MainTab.MapsNetwork or MainTab.MapsGeographical or MainTab.MapsCustom => MetricArea.Maps,
            MainTab.Intelligence => MetricArea.Intelligence,
            _ => (MetricArea?)null,
        };
        Count(area);
    }

    public void EnteredDashboardEdit() => Count(MetricArea.DashboardEditor);

    public void WidgetAdded(string? widgetType) => Count(widgetType switch
    {
        DashboardWidgetTypes.Sensors => MetricArea.WidgetSensors,
        DashboardWidgetTypes.Alerts => MetricArea.WidgetAlerts,
        DashboardWidgetTypes.AlertsGauge => MetricArea.WidgetAlertsGauge,
        DashboardWidgetTypes.DeviceStatus => MetricArea.WidgetDeviceStatus,
        DashboardWidgetTypes.RecentlyViewed => MetricArea.WidgetRecentlyViewed,
        DashboardWidgetTypes.PinnedDevices => MetricArea.WidgetPinnedDevices,
        DashboardWidgetTypes.Graph => MetricArea.WidgetGraph,
        DashboardWidgetTypes.Wireless => MetricArea.WidgetWireless,
        DashboardWidgetTypes.TopInterfaces => MetricArea.WidgetTopInterfaces,
        DashboardWidgetTypes.TopErrors => MetricArea.WidgetTopErrors,
        DashboardWidgetTypes.TopDevices => MetricArea.WidgetTopDevices,
        DashboardWidgetTypes.EventLog => MetricArea.WidgetEventLog,
        DashboardWidgetTypes.Graylog => MetricArea.WidgetGraylog,
        _ => (MetricArea?)null,
    });

    private void Count(MetricArea? area)
    {
        if (area is { } category) metrics?.Record(new(MetricName.FeatureUse, 1, Area: category));
    }
}

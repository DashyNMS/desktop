using System;
using System.Collections.Generic;
using System.Linq;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Services;

/// <summary>
/// Owns the Dashboard tab's widget layout: which widgets exist, their type,
/// title, grid position/span, and - for a Sensors widget - which sensors it
/// shows. A thin wrapper over <see cref="ISettingsStore"/>.
/// </summary>
public interface IDashboardLayoutService
{
    IReadOnlyList<DashboardWidget> Widgets { get; }

    /// <summary>Raised whenever a widget or its sensors change in any way.</summary>
    event EventHandler? Changed;

    DashboardWidget AddWidget(string widgetType, string title);

    /// <summary>Adds several widgets at the positions they already have, in one save - the welcome card's starter dashboard (#233).</summary>
    void AddWidgets(IEnumerable<DashboardWidget> widgets);

    void RemoveWidget(string id);

    /// <summary>
    /// A copy of a widget with everything it shows - its sensors, graph and
    /// options - titled "(copy)", in the next free space (#279). Null when
    /// there's no such widget.
    /// </summary>
    DashboardWidget? DuplicateWidget(string id);

    /// <summary>Every widget as it is now, copied - for Undo and Cancel (#276).</summary>
    IReadOnlyList<DashboardWidget> Snapshot();

    /// <summary>Puts the dashboard back to a <see cref="Snapshot"/>, in one save.</summary>
    void Restore(IReadOnlyList<DashboardWidget> widgets);

    void Rename(string id, string title);

    /// <summary>
    /// Persists every given widget's grid position/span in one shot - e.g.
    /// after a drag or resize displaced other widgets out of the way, so the
    /// whole affected set commits as a single save and a single
    /// <see cref="Changed"/>, not one per widget.
    /// </summary>
    void CommitLayout(IEnumerable<(string Id, int Column, int Row, int ColumnSpan, int RowSpan)> widgets);

    /// <summary>Adds a sensor to a Sensors widget. A no-op if it is already there.</summary>
    void AddSensor(string widgetId, Sensor sensor, string deviceName);

    void RemoveSensor(string widgetId, int sensorId);

    /// <summary>Sets which severities an Alerts widget shows and whether acknowledged alerts count.</summary>
    void SetAlertsFilter(string widgetId, bool showCritical, bool showWarning, bool includeAcknowledged);

    /// <summary>Sets a Graph widget's device and graph name (issue #12). Passing a null graphName leaves the device chosen but the graph itself unpicked.</summary>
    void SetGraph(string widgetId, int? deviceId, string? graphName, string? portIfName = null);

    /// <summary>Adds a Graph widget already set to one port's graph (#285), where there's room.</summary>
    DashboardWidget AddPortGraph(int deviceId, string ifName, string graphType, string title);

    void SetGraphTimeRange(string widgetId, GraphTimeRangePreset preset, DateTime? customFrom, DateTime? customTo);

    /// <summary>Sets a Top interfaces, Top errors or Top devices widget's row count, ranking and (for errors) whether quiet ports are hidden (#199-#201).</summary>
    void SetTopOptions(string widgetId, int count, DesktopNMS.Core.Devices.RankBy rankBy, bool hideQuiet);

    /// <summary>Sets an Event log or Graylog widget's options (#202, #203): how many entries, the Event log's type filter, the search, and Graylog's stream and time range.</summary>
    void SetLogOptions(string widgetId, int count, string? type, string? search, string? graylogStreamId, int graylogRangeSeconds);
}

public sealed class DashboardLayoutService : IDashboardLayoutService
{
    private readonly ISettingsStore _settings;

    public DashboardLayoutService(ISettingsStore settings)
    {
        _settings = settings;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<DashboardWidget> Widgets => _settings.Current.DashboardWidgets;

    public void AddWidgets(IEnumerable<DashboardWidget> widgets)
    {
        _settings.Current.DashboardWidgets.AddRange(widgets);
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DashboardWidget AddWidget(string widgetType, string title)
    {
        var list = _settings.Current.DashboardWidgets;
        // Beside the others where there's room (#90) - see DashboardGrid.FindFreeSpot.
        var (column, row) = DashboardGrid.FindFreeSpot(list.Select(GridItem.From), DashboardWidget.DefaultColumnSpan, DashboardWidget.DefaultRowSpan);

        var widget = new DashboardWidget
        {
            WidgetType = widgetType,
            Title = title,
            Column = column,
            Row = row,
        };

        list.Add(widget);
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return widget;
    }

    public void RemoveWidget(string id)
    {
        var list = _settings.Current.DashboardWidgets;
        if (list.RemoveAll(w => w.Id == id) == 0)
        {
            return;
        }

        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DashboardWidget? DuplicateWidget(string id)
    {
        var list = _settings.Current.DashboardWidgets;
        if (Find(id) is not { } original)
        {
            return null;
        }

        var copy = original.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Title = original.Title + " (copy)";
        (copy.Column, copy.Row) = DashboardGrid.FindFreeSpot(list.Select(GridItem.From), original.ColumnSpan, original.RowSpan);

        list.Add(copy);
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    public IReadOnlyList<DashboardWidget> Snapshot() => _settings.Current.DashboardWidgets.Select(w => w.Clone()).ToList();

    public void Restore(IReadOnlyList<DashboardWidget> widgets)
    {
        var list = _settings.Current.DashboardWidgets;
        list.Clear();
        list.AddRange(widgets.Select(w => w.Clone()));
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(string id, string title)
    {
        var widget = Find(id);
        if (widget is null || widget.Title == title)
        {
            return;
        }

        widget.Title = string.IsNullOrWhiteSpace(title) ? "Widget" : title;
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CommitLayout(IEnumerable<(string Id, int Column, int Row, int ColumnSpan, int RowSpan)> widgets)
    {
        var byId = _settings.Current.DashboardWidgets.ToDictionary(w => w.Id);
        var changed = false;

        foreach (var (id, column, row, columnSpan, rowSpan) in widgets)
        {
            if (!byId.TryGetValue(id, out var widget))
            {
                continue;
            }

            var newColumn = Math.Max(0, column);
            var newRow = Math.Max(0, row);
            var newColumnSpan = Math.Max(DashboardWidget.MinColumnSpan, columnSpan);
            var newRowSpan = Math.Max(DashboardWidget.MinRowSpan, rowSpan);

            if (widget.Column == newColumn && widget.Row == newRow && widget.ColumnSpan == newColumnSpan && widget.RowSpan == newRowSpan)
            {
                continue;
            }

            widget.Column = newColumn;
            widget.Row = newRow;
            widget.ColumnSpan = newColumnSpan;
            widget.RowSpan = newRowSpan;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddSensor(string widgetId, Sensor sensor, string deviceName)
    {
        var widget = Find(widgetId);
        if (widget is null || widget.Sensors.Any(s => s.SensorId == sensor.SensorId))
        {
            return;
        }

        widget.Sensors.Add(new PinnedSensor
        {
            SensorId = sensor.SensorId,
            DeviceId = sensor.DeviceId,
            SensorClass = sensor.SensorClass ?? string.Empty,
            DeviceName = deviceName,
            Description = sensor.Description,
        });

        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveSensor(string widgetId, int sensorId)
    {
        var widget = Find(widgetId);
        if (widget is null || widget.Sensors.RemoveAll(s => s.SensorId == sensorId) == 0)
        {
            return;
        }

        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetAlertsFilter(string widgetId, bool showCritical, bool showWarning, bool includeAcknowledged)
    {
        var widget = Find(widgetId);
        if (widget is null)
        {
            return;
        }

        if (widget.AlertsShowCritical == showCritical
            && widget.AlertsShowWarning == showWarning
            && widget.AlertsIncludeAcknowledged == includeAcknowledged)
        {
            return;
        }

        widget.AlertsShowCritical = showCritical;
        widget.AlertsShowWarning = showWarning;
        widget.AlertsIncludeAcknowledged = includeAcknowledged;
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetTopOptions(string widgetId, int count, DesktopNMS.Core.Devices.RankBy rankBy, bool hideQuiet)
    {
        var widget = Find(widgetId);
        if (widget is null || (widget.TopCount == count && widget.TopRankBy == rankBy && widget.TopHideQuiet == hideQuiet))
        {
            return;
        }

        widget.TopCount = count;
        widget.TopRankBy = rankBy;
        widget.TopHideQuiet = hideQuiet;
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetLogOptions(string widgetId, int count, string? type, string? search, string? graylogStreamId, int graylogRangeSeconds)
    {
        var widget = Find(widgetId);
        if (widget is null
            || (widget.LogCount == count
                && widget.LogType == type
                && widget.LogSearch == search
                && widget.GraylogStreamId == graylogStreamId
                && widget.GraylogRangeSeconds == graylogRangeSeconds))
        {
            return;
        }

        widget.LogCount = count;
        widget.LogType = type;
        widget.LogSearch = search;
        widget.GraylogStreamId = graylogStreamId;
        widget.GraylogRangeSeconds = graylogRangeSeconds;
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DashboardWidget AddPortGraph(int deviceId, string ifName, string graphType, string title)
    {
        var list = _settings.Current.DashboardWidgets;
        var (column, row) = DashboardGrid.FindFreeSpot(list.Select(GridItem.From), DashboardWidget.DefaultColumnSpan, DashboardWidget.DefaultRowSpan);

        var widget = new DashboardWidget
        {
            WidgetType = DashboardWidgetTypes.Graph,
            Title = title,
            Column = column,
            Row = row,
            GraphDeviceId = deviceId,
            GraphName = graphType,
            GraphPortIfName = ifName,
        };

        list.Add(widget);
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return widget;
    }

    public void SetGraph(string widgetId, int? deviceId, string? graphName, string? portIfName = null)
    {
        var widget = Find(widgetId);
        if (widget is null || (widget.GraphDeviceId == deviceId && widget.GraphName == graphName && widget.GraphPortIfName == portIfName))
        {
            return;
        }

        widget.GraphDeviceId = deviceId;
        widget.GraphName = graphName;
        widget.GraphPortIfName = portIfName;
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetGraphTimeRange(string widgetId, GraphTimeRangePreset preset, DateTime? customFrom, DateTime? customTo)
    {
        var widget = Find(widgetId);
        if (widget is null)
        {
            return;
        }

        if (widget.GraphTimeRangePreset == preset && widget.GraphCustomFrom == customFrom && widget.GraphCustomTo == customTo)
        {
            return;
        }

        widget.GraphTimeRangePreset = preset;
        widget.GraphCustomFrom = customFrom;
        widget.GraphCustomTo = customTo;
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DashboardWidget? Find(string id) => _settings.Current.DashboardWidgets.FirstOrDefault(w => w.Id == id);
}

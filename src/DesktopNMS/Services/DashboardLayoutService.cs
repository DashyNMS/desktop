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

    void RemoveWidget(string id);

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

    public DashboardWidget AddWidget(string widgetType, string title)
    {
        var list = _settings.Current.DashboardWidgets;
        var (column, row) = FindFreeSpot(list, DashboardWidget.DefaultColumnSpan, DashboardWidget.DefaultRowSpan);

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

    /// <summary>
    /// Finds the first spot on the grid, scanning row by row, where a new
    /// widget of this size would not overlap any existing one. Bounded to a
    /// generous but finite number of rows so a pathological number of
    /// existing widgets cannot search forever.
    /// </summary>
    private static (int Column, int Row) FindFreeSpot(IReadOnlyList<DashboardWidget> existing, int columnSpan, int rowSpan)
    {
        const int columns = 6;
        const int rows = 200;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                if (existing.All(w => !Overlaps(col, row, columnSpan, rowSpan, w)))
                {
                    return (col, row);
                }
            }
        }

        // Practically unreachable, but fall back to stacking below everything
        // rather than searching forever.
        var maxRow = existing.Count == 0 ? 0 : existing.Max(w => w.Row + w.RowSpan);
        return (0, maxRow);
    }

    private static bool Overlaps(int column, int row, int columnSpan, int rowSpan, DashboardWidget other)
        => column < other.Column + other.ColumnSpan
        && column + columnSpan > other.Column
        && row < other.Row + other.RowSpan
        && row + rowSpan > other.Row;

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

    private DashboardWidget? Find(string id) => _settings.Current.DashboardWidgets.FirstOrDefault(w => w.Id == id);
}

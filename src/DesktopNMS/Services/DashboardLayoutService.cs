using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Services;

/// <summary>
/// Owns the Dashboard tab's widget layout: which widgets exist, their type,
/// title, position, size, and - for a Sensors widget - which sensors it
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

    void Move(string id, double x, double y);

    void Resize(string id, double width, double height);

    /// <summary>Adds a sensor to a Sensors widget. A no-op if it is already there.</summary>
    void AddSensor(string widgetId, Sensor sensor, string deviceName);

    void RemoveSensor(string widgetId, int sensorId);
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
        var (x, y) = FindFreeSpot(list, DashboardWidget.DefaultWidth, DashboardWidget.DefaultHeight);

        var widget = new DashboardWidget
        {
            WidgetType = widgetType,
            Title = title,
            X = x,
            Y = y,
        };

        list.Add(widget);
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return widget;
    }

    /// <summary>
    /// Finds the first spot on a coarse grid where a new widget would not
    /// overlap (with <see cref="DashboardWidget.Spacing"/> to spare) any
    /// existing one. Bounded to a modest grid so a new widget always lands
    /// somewhere close to the top-left, within reach of any window size,
    /// rather than searching indefinitely and placing it off past the edge
    /// of whatever canvas the view currently has visible.
    /// </summary>
    private static (double X, double Y) FindFreeSpot(IReadOnlyList<DashboardWidget> existing, double width, double height)
    {
        const double origin = 20;
        const int columns = 3;
        const int rows = 6;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var x = origin + col * (width + DashboardWidget.Spacing);
                var y = origin + row * (height + DashboardWidget.Spacing);
                var candidate = new Rect(x, y, width, height);

                if (existing.All(w => !Overlaps(candidate, new Rect(w.X, w.Y, w.Width, w.Height))))
                {
                    return (x, y);
                }
            }
        }

        // The grid above is full (a lot of widgets); fall back to a diagonal
        // cascade rather than searching forever.
        var offset = (existing.Count % 6) * 20;
        return (origin + offset, origin + offset);
    }

    private static bool Overlaps(Rect a, Rect b)
    {
        a.Inflate(DashboardWidget.Spacing, DashboardWidget.Spacing);
        return a.IntersectsWith(b);
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

    public void Move(string id, double x, double y)
    {
        var widget = Find(id);
        if (widget is null)
        {
            return;
        }

        widget.X = Math.Max(0, x);
        widget.Y = Math.Max(0, y);
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Resize(string id, double width, double height)
    {
        var widget = Find(id);
        if (widget is null)
        {
            return;
        }

        widget.Width = Math.Max(DashboardWidget.MinWidth, width);
        widget.Height = Math.Max(DashboardWidget.MinHeight, height);
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

    private DashboardWidget? Find(string id) => _settings.Current.DashboardWidgets.FirstOrDefault(w => w.Id == id);
}

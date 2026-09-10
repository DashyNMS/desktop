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

    /// <summary>Sets which severities an Alerts widget shows and whether acknowledged alerts count.</summary>
    void SetAlertsFilter(string widgetId, bool showCritical, bool showWarning, bool includeAcknowledged);

    /// <summary>
    /// Reconciles the layout with the canvas's actual current size - see
    /// <see cref="DashboardLayoutService.EnsureFitsViewport"/>. Call whenever
    /// the Dashboard tab's canvas is shown or resized (e.g. the app moved to
    /// a different, differently-sized display).
    /// </summary>
    void EnsureFitsViewport(double viewportWidth, double viewportHeight);
}

public sealed class DashboardLayoutService : IDashboardLayoutService
{
    /// <summary>Below this, a reported size is treated as a transient layout artefact, not a real viewport.</summary>
    private const double MinPlausibleDimension = 100;

    /// <summary>How far a viewport ratio can drift from 1.0 before it counts as a real resize worth rescaling for.</summary>
    private const double ScaleTolerance = 0.02;

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

    /// <summary>
    /// Compares the canvas's actual size against the size the widgets were
    /// last laid out against (<see cref="AppSettings.DashboardCanvasWidth"/>/
    /// <see cref="AppSettings.DashboardCanvasHeight"/>). A meaningful
    /// difference (e.g. the app moved to a smaller monitor) proportionally
    /// rescales every widget's position and size so the whole layout still
    /// fits, using a single uniform factor (the smaller of the width/height
    /// ratios) so nothing overflows either dimension. Regardless of whether a
    /// rescale ran, anything still (or newly) outside the canvas afterwards -
    /// including widgets stranded from before this existed - is clamped back
    /// into view.
    /// </summary>
    public void EnsureFitsViewport(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth < MinPlausibleDimension || viewportHeight < MinPlausibleDimension)
        {
            // A transient 0x0 (or near it) mid-layout reading - never a real viewport.
            return;
        }

        var settings = _settings.Current;
        var widgets = settings.DashboardWidgets;
        var referenceWidth = settings.DashboardCanvasWidth;
        var referenceHeight = settings.DashboardCanvasHeight;
        var hasReference = referenceWidth > 0 && referenceHeight > 0;
        var changed = !hasReference;

        if (hasReference && widgets.Count > 0)
        {
            var widthRatio = viewportWidth / referenceWidth;
            var heightRatio = viewportHeight / referenceHeight;

            if (Math.Abs(widthRatio - 1) > ScaleTolerance || Math.Abs(heightRatio - 1) > ScaleTolerance)
            {
                var scale = Math.Min(widthRatio, heightRatio);

                foreach (var widget in widgets)
                {
                    widget.X *= scale;
                    widget.Y *= scale;
                    widget.Width = Math.Max(DashboardWidget.MinWidth, widget.Width * scale);
                    widget.Height = Math.Max(DashboardWidget.MinHeight, widget.Height * scale);
                }

                changed = true;
            }
        }

        foreach (var widget in widgets)
        {
            var maxX = Math.Max(0, viewportWidth - widget.Width);
            var maxY = Math.Max(0, viewportHeight - widget.Height);
            var clampedX = Math.Clamp(widget.X, 0, maxX);
            var clampedY = Math.Clamp(widget.Y, 0, maxY);

            if (Math.Abs(clampedX - widget.X) > 0.5 || Math.Abs(clampedY - widget.Y) > 0.5)
            {
                widget.X = clampedX;
                widget.Y = clampedY;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        settings.DashboardCanvasWidth = viewportWidth;
        settings.DashboardCanvasHeight = viewportHeight;
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DashboardWidget? Find(string id) => _settings.Current.DashboardWidgets.FirstOrDefault(w => w.Id == id);
}

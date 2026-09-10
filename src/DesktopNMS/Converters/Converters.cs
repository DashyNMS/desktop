using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Converters;

/// <summary>Maps an <see cref="AlertSeverity"/> to the accent brush for that severity.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush Critical = Frozen(Color.FromRgb(0xDA, 0x36, 0x33));
    public static readonly SolidColorBrush Warning = Frozen(Color.FromRgb(0xDB, 0x9A, 0x04));
    public static readonly SolidColorBrush Ok = Frozen(Color.FromRgb(0x2E, 0xA0, 0x43));
    public static readonly SolidColorBrush Unknown = Frozen(Color.FromRgb(0x7D, 0x85, 0x90));
    public static readonly SolidColorBrush Maintenance = Frozen(Color.FromRgb(0x4C, 0x9A, 0xFF));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AlertSeverity.Critical => Critical,
        AlertSeverity.Warning => Warning,
        AlertSeverity.Ok => Ok,
        _ => Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}

/// <summary>true becomes Visible, false becomes Collapsed. Pass "invert" to reverse it.</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Non-empty string becomes Visible.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Null becomes Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Maps a <see cref="DeviceState"/> to the accent brush for that state.</summary>
public sealed class DeviceStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DeviceState.Up => SeverityToBrushConverter.Ok,
        DeviceState.Down => SeverityToBrushConverter.Critical,
        DeviceState.Maintenance => SeverityToBrushConverter.Maintenance,
        _ => SeverityToBrushConverter.Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Dims recovered and acknowledged rows so the eye goes to what is still on fire.
/// </summary>
public sealed class AlertStateToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AlertState.Recovered => 0.45,
        AlertState.Acknowledged => 0.72,
        _ => 1.0,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Turns a 0-1 fraction into a clockwise ring-gauge arc starting at 12
/// o'clock, in a fixed 100x100 local coordinate space (the Path is expected
/// to sit inside a Viewbox so it scales to whatever size the widget gives it).
/// A fraction of 0 renders nothing; fractions are clamped just short of a
/// full turn so the start and end points of the arc never coincide, which
/// would otherwise make WPF drop the segment.
/// </summary>
public sealed class GaugeArcConverter : IValueConverter
{
    private const double CenterX = 50;
    private const double CenterY = 50;
    private const double Radius = 40;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? Math.Clamp(d, 0, 0.9999) : 0.0;

        if (fraction <= 0)
        {
            return Geometry.Empty;
        }

        const double startAngle = -90;
        var sweepAngle = fraction * 360;

        var start = PointOnCircle(startAngle);
        var end = PointOnCircle(startAngle + sweepAngle);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(Radius, Radius), 0, sweepAngle > 180, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Point PointOnCircle(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new Point(CenterX + (Radius * Math.Cos(radians)), CenterY + (Radius * Math.Sin(radians)));
    }
}

/// <summary>Scales an integer dashboard grid unit (column/row/span) up to pixels by <see cref="DashboardWidget.CellSize"/>.</summary>
public sealed class GridUnitToPixelsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int units ? units * DashboardWidget.CellSize : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

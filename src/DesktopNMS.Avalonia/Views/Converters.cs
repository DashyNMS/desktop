using System;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Svg.Skia;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;
using FluentIcons.Common;

namespace DesktopNMS.Views;

/// <summary>Small converters the views share (#250).</summary>
public static class Converters
{
    /// <summary>Drawn shapes avoid font bearings moving the outcome off-centre.</summary>
    public static readonly IValueConverter CompletionIcon = new FuncValueConverter<string?, Symbol>(glyph => glyph switch
    {
        "■" => Symbol.Stop,
        "!" => Symbol.Important,
        _ => Symbol.Checkmark,
    });

    private static readonly IBrush CriticalBrush = new ImmutableSolidColorBrush(Color.Parse("#DA3633"));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse("#DB9A04"));
    private static readonly IBrush OkBrush = new ImmutableSolidColorBrush(Color.Parse("#2EA043"));
    private static readonly IBrush UnknownBrush = new ImmutableSolidColorBrush(Color.Parse("#7D8590"));
    private static readonly IBrush MaintenanceBrush = new ImmutableSolidColorBrush(Color.Parse("#4C9AFF"));

    /// <summary>An alert severity's colour: a status dot, a sensor's value.</summary>
    public static readonly IValueConverter SeverityToBrush = new FuncValueConverter<AlertSeverity?, IBrush>(severity => severity switch
    {
        AlertSeverity.Critical => CriticalBrush,
        AlertSeverity.Warning => WarningBrush,
        AlertSeverity.Ok => OkBrush,
        _ => UnknownBrush,
    });

    /// <summary>A routing session's health as its dot: fine, worth a look, down, or shut down on purpose.</summary>
    public static readonly IValueConverter RoutingHealthToBrush = new FuncValueConverter<RoutingHealth, IBrush>(health => health switch
    {
        RoutingHealth.Warning => WarningBrush,
        RoutingHealth.Critical => CriticalBrush,
        RoutingHealth.Disabled => UnknownBrush,
        _ => OkBrush,
    });

    /// <summary>A Graylog message's colour band (GraylogMessageItemViewModel.Severity) as its dot: info is blue, as in LibreNMS.</summary>
    public static readonly IValueConverter GraylogSeverityToBrush = new FuncValueConverter<string?, IBrush>(severity => severity switch
    {
        "Critical" => CriticalBrush,
        "Warning" => WarningBrush,
        "Debug" => UnknownBrush,
        _ => MaintenanceBrush,
    });

    /// <summary>Intelligence's status words ("ok", "down", "warning") as a dot's colour.</summary>
    public static readonly IValueConverter StatusToneToBrush = new FuncValueConverter<string?, IBrush>(tone => tone switch
    {
        "ok" => OkBrush,
        "down" => CriticalBrush,
        "warning" => WarningBrush,
        _ => UnknownBrush,
    });

    /// <summary>A device's state as a status dot's colour.</summary>
    public static readonly IValueConverter DeviceStateToBrush = new FuncValueConverter<DeviceState?, IBrush>(StateBrush);

    /// <summary>A device's state as a colour, for what draws its own dots - the maps.</summary>
    public static IBrush StateBrush(DeviceState? state) => state switch
    {
        DeviceState.Up => OkBrush,
        DeviceState.Down => CriticalBrush,
        DeviceState.Maintenance => MaintenanceBrush,
        _ => UnknownBrush,
    };

    /// <summary>An alert faded by its state: recovered most, acknowledged a little, active not at all.</summary>
    public static readonly IValueConverter AlertStateToOpacity = new FuncValueConverter<AlertState, double>(state => state switch
    {
        AlertState.Recovered => 0.45,
        AlertState.Acknowledged => 0.72,
        _ => 1.0,
    });

    /// <summary>A tree row's depth as its indent, 18 px a level.</summary>
    public static readonly IValueConverter DepthToIndent = new FuncValueConverter<int, Thickness>(depth => new Thickness(depth * 18, 0, 0, 0));

    /// <summary>Fades a row while a change to it is on its way.</summary>
    public static readonly IValueConverter FadeWhenBusy = new FuncValueConverter<bool, double>(busy => busy ? 0.45 : 1.0);

    /// <summary>Dims what's true - an acknowledged alert - as on the Alerts tab.</summary>
    public static readonly IValueConverter DimWhen = new FuncValueConverter<bool, double>(dim => dim ? 0.6 : 1.0);

    /// <summary>A sentence as a heading: "No interface errors." as "No interface errors".</summary>
    public static readonly IValueConverter WithoutFullStop = new FuncValueConverter<string?, string?>(text => text?.TrimEnd().TrimEnd('.'));

    /// <summary>True for a count of nothing: an empty list's message.</summary>
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(count => count == 0);

    /// <summary>Whole Dashboard grid cells as pixels.</summary>
    public static readonly IValueConverter GridUnitToPixels = new FuncValueConverter<int, double>(units => units * DashboardWidget.CellSize);

    /// <summary>
    /// A 0-1 fraction as a ring gauge's arc, in a 100x100 space (radius 40,
    /// centred), clockwise from twelve o'clock - the gauges' Viewbox scales it.
    /// </summary>
    public static readonly IValueConverter GaugeArc = new FuncValueConverter<double, Geometry?>(fraction =>
    {
        fraction = Math.Clamp(fraction, 0, 0.9999);
        if (fraction <= 0)
        {
            return null;
        }

        const double radius = 40;
        var centre = new Point(50, 50);
        var start = PointAt(-90);
        var sweep = fraction * 360;
        var end = PointAt(-90 + sweep);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false);
            context.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
            context.EndFigure(isClosed: false);
        }

        return geometry;

        Point PointAt(double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(centre.X + radius * Math.Cos(radians), centre.Y + radius * Math.Sin(radians));
        }
    });

    /// <summary>An SVG document's text (LibreNMS's graphs) as an image; nothing until there is one.</summary>
    public static readonly IValueConverter SvgToImage = new FuncValueConverter<string?, IImage?>(svg =>
    {
        if (string.IsNullOrWhiteSpace(svg))
        {
            return null;
        }

        try
        {
            return SvgSource.LoadFromSvg(svg) is { } source ? new SvgImage { Source = source } : null;
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>"#3B82F6" as a brush; anything unreadable as transparent.</summary>
    public static readonly IValueConverter HexToBrush = new FuncValueConverter<string?, IBrush>(
        hex => Color.TryParse(hex, out var colour) ? new SolidColorBrush(colour) : Brushes.Transparent);

    /// <summary>A device's LibreNMS type ("server", "wireless") as its icon - the WPF app's TypeGlyph, as Fluent icons.</summary>
    public static readonly IValueConverter DeviceTypeIcon = new FuncValueConverter<string?, Symbol>(type => (type ?? string.Empty).ToLowerInvariant() switch
    {
        "server" => Symbol.Server,
        "wireless" => Symbol.WiFi1,
        "firewall" => Symbol.Shield,
        "power" => Symbol.Flash,
        "storage" => Symbol.Database,
        "printer" => Symbol.Print,
        "appliance" or "workstation" => Symbol.Desktop,
        _ => Symbol.Router,
    });

    /// <summary>What's New's icon names (WhatsNew.md's trailing "{name}") as Fluent icons.</summary>
    public static readonly IValueConverter WhatsNewIcon = new FuncValueConverter<string?, Symbol>(name => name switch
    {
        "map" => Symbol.Map,
        "link" => Symbol.Link,
        "logs" => Symbol.TextBulletList,
        "copy" => Symbol.Copy,
        "add" => Symbol.Add,
        "swap" => Symbol.ArrowSwap,
        "chart" => Symbol.DataLine,
        "download" => Symbol.ArrowDownload,
        "look" => Symbol.Eye,
        "grid" => Symbol.Grid,
        "tray" => Symbol.TrayItemAdd,
        "lock" => Symbol.LockClosed,
        "speed" => Symbol.TopSpeed,
        "clock" => Symbol.Clock,
        "shield" => Symbol.Shield,
        "signout" => Symbol.SignOut,
        "package" => Symbol.Box,
        "bell" => Symbol.Alert,
        "device" => Symbol.Server,
        "settings" => Symbol.Settings,
        "search" => Symbol.Search,
        "wrench" => Symbol.Wrench,
        "check" => Symbol.Checkmark,
        _ => Symbol.Star,
    });
}

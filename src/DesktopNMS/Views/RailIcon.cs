using System.Windows;
using System.Windows.Media;

namespace DesktopNMS.Views;

/// <summary>
/// A sidebar item's icon as a shape rather than a font glyph - drawn as an
/// outline in the item's own colour, so it greys, lightens and turns accent
/// with the glyph icons around it:
/// <code>&lt;ToggleButton Style="{StaticResource MainRailItemStyle}" views:RailIcon.Geometry="{x:Static views:RailIcon.DashyMark}" /&gt;</code>
/// </summary>
public static class RailIcon
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.RegisterAttached(
        "Geometry",
        typeof(Geometry),
        typeof(RailIcon),
        new PropertyMetadata(null));

    /// <summary>
    /// The DashyNMS mark as an outline: its ring, and the heartbeat through it
    /// (the same trace as <see cref="DesktopNMS.Core.Branding.HeartbeatTrace"/>),
    /// in the logo's own 100×100 space.
    /// </summary>
    public static Geometry DashyMark { get; } = CreateDashyMark();

    public static Geometry? GetGeometry(DependencyObject element) => (Geometry?)element.GetValue(GeometryProperty);

    public static void SetGeometry(DependencyObject element, Geometry? value) => element.SetValue(GeometryProperty, value);

    private static Geometry CreateDashyMark()
    {
        var group = new GeometryGroup();
        group.Children.Add(new EllipseGeometry(new Point(50, 50), 42.25, 42.25));

        var trace = new StreamGeometry();
        using (var context = trace.Open())
        {
            var points = DesktopNMS.Core.Branding.HeartbeatTrace.Points;
            context.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false, isClosed: false);
            for (var i = 1; i < points.Count; i++)
            {
                context.LineTo(new Point(points[i].X, points[i].Y), isStroked: true, isSmoothJoin: true);
            }
        }

        group.Children.Add(trace);
        group.Freeze();
        return group;
    }
}

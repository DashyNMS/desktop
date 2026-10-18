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
    /// Intelligence's mark: the DashyNMS heartbeat (the logo's trace, see
    /// <see cref="DesktopNMS.Core.Branding.HeartbeatTrace"/>) inside a
    /// rounded-square chat bubble, as an outline in the logo's 100×100 space.
    /// </summary>
    public static Geometry DashyMark { get; } = CreateDashyMark();

    public static Geometry? GetGeometry(DependencyObject element) => (Geometry?)element.GetValue(GeometryProperty);

    public static void SetGeometry(DependencyObject element, Geometry? value) => element.SetValue(GeometryProperty, value);

    private static Geometry CreateDashyMark()
    {
        var group = new GeometryGroup();

        // The bubble, its tail down at the bottom left.
        group.Children.Add(Geometry.Parse("M 20 12 H 80 Q 92 12 92 24 V 64 Q 92 76 80 76 H 44 L 24 92 L 28 76 H 20 Q 8 76 8 64 V 24 Q 8 12 20 12 Z"));

        // The logo's heartbeat, raised to the bubble's middle (above the tail).
        var trace = new StreamGeometry();
        using (var context = trace.Open())
        {
            var points = DesktopNMS.Core.Branding.HeartbeatTrace.Points;
            const double raise = 8;
            context.BeginFigure(new Point(points[0].X, points[0].Y - raise), isFilled: false, isClosed: false);
            for (var i = 1; i < points.Count; i++)
            {
                context.LineTo(new Point(points[i].X, points[i].Y - raise), isStroked: true, isSmoothJoin: true);
            }
        }

        group.Children.Add(trace);
        group.Freeze();
        return group;
    }
}

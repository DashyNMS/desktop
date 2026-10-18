using System.Windows;
using System.Windows.Media;

namespace DesktopNMS.Views;

/// <summary>
/// A sidebar item's icon as a shape rather than a font glyph - drawn as an
/// outline in the item's own colour, so it greys, lightens and turns accent
/// with the glyph icons around it:
/// <code>&lt;ToggleButton Style="{StaticResource MainRailItemStyle}" views:RailIcon.Geometry="{x:Static views:RailIcon.RouteSparkle}" /&gt;</code>
/// </summary>
public static class RailIcon
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.RegisterAttached(
        "Geometry",
        typeof(Geometry),
        typeof(RailIcon),
        new PropertyMetadata(null));

    /// <summary>
    /// Intelligence's mark: two routed devices with a four-point sparkle.
    /// One shared outline keeps the sidebar and chat header consistent.
    /// </summary>
    public static Geometry RouteSparkle { get; } = CreateRouteSparkle();

    public static Geometry? GetGeometry(DependencyObject element) => (Geometry?)element.GetValue(GeometryProperty);

    public static void SetGeometry(DependencyObject element, Geometry? value) => element.SetValue(GeometryProperty, value);

    private static Geometry CreateRouteSparkle()
    {
        var group = new GeometryGroup();
        group.Children.Add(new RectangleGeometry(new Rect(1.5, 4, 6, 6), 1.2, 1.2));
        group.Children.Add(new RectangleGeometry(new Rect(12, 14, 6, 6), 1.2, 1.2));
        group.Children.Add(Geometry.Parse("M 4.5 10 V 15 Q 4.5 17 6.5 17 H 12"));
        group.Children.Add(Geometry.Parse("M 16 0 C 16.8 4 18 5.2 22 6 C 18 6.8 16.8 8 16 12 C 15.2 8 14 6.8 10 6 C 14 5.2 15.2 4 16 0 Z"));
        group.Freeze();
        return group;
    }
}

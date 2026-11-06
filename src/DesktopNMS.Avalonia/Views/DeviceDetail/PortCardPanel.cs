using Avalonia;
using Avalonia.Controls;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>
/// Lays the Ports tab's cards out several to a row (#569): as many columns as
/// fit at <see cref="MinItemWidth"/>, then every column widened to share the
/// row, so the cards fill the width with no ragged gap at the right - which a
/// WrapPanel's fixed item width would leave. Each row is as tall as its
/// tallest card, so the cards in a row line up.
/// </summary>
public sealed class PortCardPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<PortCardPanel, double>(nameof(MinItemWidth), 320);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<PortCardPanel, double>(nameof(Spacing), 12);

    static PortCardPanel()
    {
        AffectsMeasure<PortCardPanel>(MinItemWidthProperty, SpacingProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int Columns(double width) =>
        double.IsFinite(width) && width > 0
            ? Math.Max(1, (int)((width + Spacing) / (Math.Max(1, MinItemWidth) + Spacing)))
            : 1;

    private double ItemWidth(double width, int columns) =>
        double.IsFinite(width) && width > 0
            ? Math.Max(0, (width - (Spacing * (columns - 1))) / columns)
            : MinItemWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var itemWidth = ItemWidth(availableSize.Width, columns);
        var height = 0.0;
        var rowHeight = 0.0;

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

            if (i % columns == columns - 1 || i == Children.Count - 1)
            {
                height += rowHeight + (i == Children.Count - 1 ? 0 : Spacing);
                rowHeight = 0;
            }
        }

        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : (itemWidth * columns) + (Spacing * (columns - 1));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var itemWidth = ItemWidth(finalSize.Width, columns);
        var y = 0.0;

        for (var rowStart = 0; rowStart < Children.Count; rowStart += columns)
        {
            var rowEnd = Math.Min(rowStart + columns, Children.Count);
            var rowHeight = 0.0;
            for (var i = rowStart; i < rowEnd; i++)
            {
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }

            for (var i = rowStart; i < rowEnd; i++)
            {
                var x = (i - rowStart) * (itemWidth + Spacing);
                Children[i].Arrange(new Rect(x, y, itemWidth, rowHeight));
            }

            y += rowHeight + Spacing;
        }

        return finalSize;
    }
}

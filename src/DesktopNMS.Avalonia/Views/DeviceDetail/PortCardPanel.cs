using Avalonia;
using Avalonia.Controls;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>
/// Lays the Ports tab's cards out several to a row (#569): as many columns as
/// fit at <see cref="MinItemWidth"/>, then every column widened to share the
/// row, so the cards fill the width with no ragged gap at the right - which a
/// WrapPanel's fixed item width would leave. Each row is as tall as its
/// tallest card, so the cards in a row line up.
/// </summary>
/// <remarks>
/// The card list is virtualised by rows (#572), so each panel now holds one
/// row and is told its <see cref="Columns"/>: a group's short last row has to
/// keep the full rows' card width rather than spread its few cards across.
/// </remarks>
public sealed class PortCardPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<PortCardPanel, double>(nameof(MinItemWidth), 320);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<PortCardPanel, double>(nameof(Spacing), 12);

    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<PortCardPanel, int>(nameof(Columns));

    static PortCardPanel()
    {
        AffectsMeasure<PortCardPanel>(MinItemWidthProperty, SpacingProperty, ColumnsProperty);
    }

    /// <summary>Cards across; zero or less fits as many as <see cref="MinItemWidth"/> allows.</summary>
    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
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

    private int ColumnsFor(double width) =>
        Columns > 0 ? Columns : PortCardRowLayout.ColumnsFor(width, MinItemWidth, Spacing);

    private double ItemWidth(double width, int columns) =>
        double.IsFinite(width) && width > 0
            ? Math.Max(0, (width - (Spacing * (columns - 1))) / columns)
            : MinItemWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnsFor(availableSize.Width);
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
        var columns = ColumnsFor(finalSize.Width);
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

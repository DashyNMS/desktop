using Avalonia;
using Avalonia.Controls;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>Responsive overview cards without the empty rows of nested grids.</summary>
public sealed class OverviewPanel : Panel
{
    public static readonly AttachedProperty<int> ColumnSpanProperty =
        AvaloniaProperty.RegisterAttached<OverviewPanel, Control, int>("ColumnSpan", 1);

    static OverviewPanel() => AffectsParentMeasure<OverviewPanel>(ColumnSpanProperty);

    public static int GetColumnSpan(Control control) => control.GetValue(ColumnSpanProperty);
    public static void SetColumnSpan(Control control, int value) => control.SetValue(ColumnSpanProperty, value);

    private OverviewLayoutPlan.Placement[] Plan(double width, Control[] cards) =>
        OverviewLayoutPlan.Create(width, cards.Select(card => (GetColumnSpan(card), (Func<double, double>)(w =>
        {
            card.Measure(new Size(w, double.PositiveInfinity));
            return card.DesiredSize.Height;
        }))).ToArray());

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : OverviewLayoutPlan.MinimumColumnWidth;
        var slots = Plan(width, Children.Where(child => child.IsVisible).ToArray());
        return new Size(width, slots.Length == 0 ? 0 : slots.Max(slot => slot.Y + slot.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cards = Children.Where(child => child.IsVisible).ToArray();
        var slots = Plan(finalSize.Width, cards);
        for (var i = 0; i < cards.Length; i++)
        {
            var slot = slots[i];
            cards[i].Arrange(new Rect(slot.X, slot.Y, slot.Width, slot.Height));
        }
        return finalSize;
    }
}

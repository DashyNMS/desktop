namespace DesktopNMS.Views.DeviceDetail;

/// <summary>Places measured overview cards into the shortest available columns.</summary>
internal static class OverviewLayoutPlan
{
    internal const double Gap = 14;
    internal const double MinimumColumnWidth = 260;

    internal readonly record struct Placement(double X, double Y, double Width, double Height);

    internal static Placement[] Create(double width, IReadOnlyList<(int Span, Func<double, double> Measure)> cards)
    {
        var columns = Math.Clamp((int)((width + Gap) / (MinimumColumnWidth + Gap)), 1, 3);
        var columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var bottoms = new double[columns];
        var result = new Placement[cards.Count];
        for (var i = 0; i < cards.Count; i++)
        {
            var span = Math.Clamp(cards[i].Span, 1, columns);
            var bestColumn = 0;
            var bestY = double.PositiveInfinity;
            for (var column = 0; column <= columns - span; column++)
            {
                var y = bottoms.Skip(column).Take(span).Max();
                if (y < bestY) { bestY = y; bestColumn = column; }
            }
            var cardWidth = columnWidth * span + Gap * (span - 1);
            var height = cards[i].Measure(cardWidth);
            result[i] = new(bestColumn * (columnWidth + Gap), bestY, cardWidth, height);
            for (var column = bestColumn; column < bestColumn + span; column++)
                bottoms[column] = bestY + height + Gap;
        }
        return result;
    }
}

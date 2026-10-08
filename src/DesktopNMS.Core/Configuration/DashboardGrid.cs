namespace DesktopNMS.Core.Configuration;

/// <summary>A widget's place on the dashboard grid, in cells - what the arranging below works on.</summary>
public sealed class GridItem
{
    public GridItem(string id, int column, int row, int columnSpan, int rowSpan)
    {
        Id = id;
        Column = column;
        Row = row;
        ColumnSpan = columnSpan;
        RowSpan = rowSpan;
    }

    public string Id { get; }

    public int Column { get; set; }

    public int Row { get; set; }

    public int ColumnSpan { get; set; }

    public int RowSpan { get; set; }

    public static GridItem From(DashboardWidget widget) => new(widget.Id, widget.Column, widget.Row, widget.ColumnSpan, widget.RowSpan);
}

/// <summary>A widget size to pick instead of dragging a corner (#278).</summary>
public enum WidgetSize
{
    Small,
    Medium,
    Large,
    Wide,
}

/// <summary>
/// Arranging widgets on the dashboard's grid (#274-#279): where a new one
/// fits, pushing others out of the way, closing gaps, the sizes, and which
/// edges line up while dragging. Pure, so it can be tested - the desktop's
/// drag and resize live on top of it.
/// </summary>
public static class DashboardGrid
{
    /// <summary>
    /// How wide the search for a free space looks - three default-width
    /// widgets, so new ones sit side by side rather than in one column down
    /// the left (#90).
    /// </summary>
    public const int ScanColumns = DashboardWidget.DefaultColumnSpan * 3;

    // Backstops: a finite search, and a cascade that can't run away.
    private const int ScanRows = 200;
    private const int MaxReflowIterations = 2000;

    public static bool Overlaps(GridItem a, GridItem b)
        => a.Column < b.Column + b.ColumnSpan
        && a.Column + a.ColumnSpan > b.Column
        && a.Row < b.Row + b.RowSpan
        && a.Row + a.RowSpan > b.Row;

    /// <summary>The first space, row by row, where a widget this size overlaps nothing.</summary>
    public static (int Column, int Row) FindFreeSpot(IEnumerable<GridItem> existing, int columnSpan, int rowSpan)
    {
        var items = existing.ToList();
        for (var row = 0; row < ScanRows; row++)
        {
            for (var column = 0; column + columnSpan <= Math.Max(ScanColumns, columnSpan); column++)
            {
                var candidate = new GridItem(string.Empty, column, row, columnSpan, rowSpan);
                if (items.All(other => !Overlaps(candidate, other)))
                {
                    return (column, row);
                }
            }
        }

        // Practically unreachable: under everything.
        return (0, items.Count == 0 ? 0 : items.Max(w => w.Row + w.RowSpan));
    }

    /// <summary>
    /// Pushes whatever <paramref name="anchor"/> now overlaps straight down
    /// to clear it, then whatever those overlap, and so on - so moving or
    /// growing a widget reflows the rest instead of being blocked. Only rows
    /// change, and only downwards, so it always ends.
    /// </summary>
    public static void PushDown(GridItem anchor, IReadOnlyList<GridItem> all)
    {
        var queue = new Queue<GridItem>();
        queue.Enqueue(anchor);

        var iterations = 0;
        while (queue.Count > 0 && iterations++ < MaxReflowIterations)
        {
            var current = queue.Dequeue();
            foreach (var other in all)
            {
                if (ReferenceEquals(other, current) || ReferenceEquals(other, anchor) || !Overlaps(current, other))
                {
                    continue;
                }

                var below = current.Row + current.RowSpan;
                if (other.Row < below)
                {
                    other.Row = below;
                    queue.Enqueue(other);
                }
            }
        }
    }

    /// <summary>
    /// "Tidy up" (#278): every widget moves straight up as far as it can,
    /// top-most first, closing the gaps without changing the order or any
    /// widget's column or size.
    /// </summary>
    public static void TidyUp(IReadOnlyList<GridItem> all)
    {
        var placed = new List<GridItem>();
        foreach (var item in all.OrderBy(i => i.Row).ThenBy(i => i.Column))
        {
            while (item.Row > 0)
            {
                item.Row--;
                if (placed.Any(p => Overlaps(item, p)))
                {
                    item.Row++;
                    break;
                }
            }

            placed.Add(item);
        }
    }

    /// <summary>The cells a size takes: Small is the smallest a widget can be, Medium the size a new one gets.</summary>
    public static (int ColumnSpan, int RowSpan) SpanFor(WidgetSize size) => size switch
    {
        WidgetSize.Small => (DashboardWidget.MinColumnSpan, DashboardWidget.MinRowSpan),
        WidgetSize.Medium => (DashboardWidget.DefaultColumnSpan, DashboardWidget.DefaultRowSpan),
        WidgetSize.Large => (15, 10),
        _ => (20, 6),
    };

    /// <summary>The size a widget is now, if it's exactly one of the sizes.</summary>
    public static WidgetSize? SizeOf(int columnSpan, int rowSpan)
        => Enum.GetValues<WidgetSize>().Cast<WidgetSize?>().FirstOrDefault(s => SpanFor(s!.Value) == (columnSpan, rowSpan));

    /// <summary>
    /// The grid lines where <paramref name="moving"/>'s edges meet another
    /// widget's same-side or opposite edges - the alignment guides shown
    /// while dragging or resizing (#278). Columns for vertical lines, rows
    /// for horizontal ones.
    /// </summary>
    public static (IReadOnlyList<int> Columns, IReadOnlyList<int> Rows) AlignedEdges(GridItem moving, IEnumerable<GridItem> others)
    {
        var columns = new SortedSet<int>();
        var rows = new SortedSet<int>();
        int[] myColumns = [moving.Column, moving.Column + moving.ColumnSpan];
        int[] myRows = [moving.Row, moving.Row + moving.RowSpan];

        foreach (var other in others.Where(o => o.Id != moving.Id))
        {
            foreach (var edge in new[] { other.Column, other.Column + other.ColumnSpan }.Where(e => myColumns.Contains(e)))
            {
                columns.Add(edge);
            }

            foreach (var edge in new[] { other.Row, other.Row + other.RowSpan }.Where(e => myRows.Contains(e)))
            {
                rows.Add(edge);
            }
        }

        return (columns.ToList(), rows.ToList());
    }
}

using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>Device Details' Alerts and Event log.</summary>
public partial class AlertsLogSections : UserControl
{
    /// <summary>How close to the end (in pixels) loads the next page, so it starts a little before the end is reached.</summary>
    private const double LoadMoreThreshold = 200;

    private ScrollBar? _eventLogScrollBar;

    public AlertsLogSections()
    {
        InitializeComponent();

        // An entry picked from elsewhere - a toast, the Alerts tab - is scrolled to.
        AlertHistoryGrid.SelectionChanged += (_, _) => ScrollToSelected(AlertHistoryGrid);
        EventLogGrid.SelectionChanged += (_, _) => ScrollToSelected(EventLogGrid);

        // The event log's next page loads as it's scrolled near the end: the
        // grid's own scrollbar is watched, the DataGrid having no ScrollViewer.
        EventLogGrid.LayoutUpdated += (_, _) => HookEventLogScrollBar();
    }

    private static void ScrollToSelected(DataGrid grid)
    {
        if (grid.SelectedItem is { } item)
        {
            grid.ScrollIntoView(item, null);
        }
    }

    private void HookEventLogScrollBar()
    {
        if (_eventLogScrollBar is not null)
        {
            return;
        }

        _eventLogScrollBar = EventLogGrid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(s => s.Orientation == Avalonia.Layout.Orientation.Vertical);
        if (_eventLogScrollBar is not null)
        {
            _eventLogScrollBar.PropertyChanged += OnEventLogScrollBarChanged;
        }
    }

    private void OnEventLogScrollBarChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty || sender is not ScrollBar bar)
        {
            return;
        }

        // Only once the rows overflow the view: an empty grid is "at the end" without being scrolled there.
        var nearEnd = bar.Maximum > 0 && bar.Value >= bar.Maximum - LoadMoreThreshold;
        if (nearEnd && DataContext is DeviceDetailViewModel viewModel && viewModel.LoadMoreEventLogCommand.CanExecute(null))
        {
            viewModel.LoadMoreEventLogCommand.Execute(null);
        }
    }
}

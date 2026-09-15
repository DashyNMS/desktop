using System.Windows;
using System.Windows.Controls;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// A single device's detail window, opened from the Devices tab (double-click
/// or the row's "device view" action) instead of jumping straight to the
/// LibreNMS website. Non-modal - see <see cref="Services.WindowService.ShowDeviceDetail"/> -
/// so several can be open alongside the main window and each other.
/// </summary>
public partial class DeviceView : Window
{
    /// <summary>How close to the bottom (in pixels) triggers loading the next page, so it fires a little before the user actually hits the end.</summary>
    private const double EventLogLoadMoreThreshold = 200;

    public DeviceView(DeviceDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // ScrollChanged bubbles up from the DataGrid's own internal
        // ScrollViewer as a routed event, so this catches it without needing
        // to reach into the DataGrid's template to find that ScrollViewer.
        EventLogGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnEventLogScrollChanged));
    }

    private void OnEventLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The grid fires an initial ScrollChanged while still empty (before
        // the first page has even loaded), where ExtentHeight is 0 and the
        // "near bottom" check below is trivially true - requiring the content
        // to actually overflow the viewport rules that out, rather than
        // treating "nothing to scroll yet" as "scrolled to the end".
        var hasOverflow = e.ExtentHeight > e.ViewportHeight;
        var nearBottom = hasOverflow && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - EventLogLoadMoreThreshold;

        if (nearBottom && DataContext is DeviceDetailViewModel viewModel && viewModel.LoadMoreEventLogCommand.CanExecute(null))
        {
            viewModel.LoadMoreEventLogCommand.Execute(null);
        }
    }
}

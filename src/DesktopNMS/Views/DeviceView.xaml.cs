using System.ComponentModel;
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

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// PasswordBox's own text survives navigating away from and back to Edit
    /// even though SelectEdit() resets the view model's own EditAuthPass/
    /// EditCryptoPass to empty - a PasswordBox is not bindable, so nothing
    /// else would ever clear what it visibly shows. Clearing it here keeps
    /// the box honest about the (reset) state the view model is actually in.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceDetailViewModel.IsEditSelected)
            && DataContext is DeviceDetailViewModel { IsEditSelected: true })
        {
            EditAuthPassBox.Password = string.Empty;
            EditCryptoPassBox.Password = string.Empty;
        }
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

    /// <summary>"Open in" opens its Web/Telnet/SSH picker menu on a left click, not just the usual right click.</summary>
    private void OnOpenInClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// PasswordBox does not expose a bindable password, by design, so the
    /// value is pushed to the view model here instead - same as
    /// AddDeviceWindow's own SNMP v3 password fields.
    /// </summary>
    private void OnEditAuthPassChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceDetailViewModel viewModel)
        {
            viewModel.EditAuthPass = EditAuthPassBox.Password;
        }
    }

    private void OnEditCryptoPassChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceDetailViewModel viewModel)
        {
            viewModel.EditCryptoPass = EditCryptoPassBox.Password;
        }
    }
}

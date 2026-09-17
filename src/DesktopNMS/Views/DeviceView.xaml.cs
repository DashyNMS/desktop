using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
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

    private readonly ISettingsStore _settings;

    public DeviceView(DeviceDetailViewModel viewModel, ISettingsStore settings)
    {
        _settings = settings;

        InitializeComponent();
        DataContext = viewModel;

        // ScrollChanged bubbles up from the DataGrid's own internal
        // ScrollViewer as a routed event, so this catches it without needing
        // to reach into the DataGrid's template to find that ScrollViewer.
        EventLogGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnEventLogScrollChanged));

        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        ApplyGridLayouts();
    }

    /// <summary>
    /// Restores each sub-table's remembered column widths/order/sort (issue
    /// #15) - a shared preference across every device window, not per-device
    /// data, so a newly opened window for a different device still reflects
    /// whatever was last saved.
    /// </summary>
    private void ApplyGridLayouts()
    {
        var layouts = _settings.Current.GridLayouts;

        DataGridLayoutHelper.Apply(PortsGrid, layouts.GetValueOrDefault("DeviceDetail.Ports"));
        DataGridLayoutHelper.Apply(VlansGrid, layouts.GetValueOrDefault("DeviceDetail.Vlans"));
        DataGridLayoutHelper.Apply(FdbGrid, layouts.GetValueOrDefault("DeviceDetail.Fdb"));
        DataGridLayoutHelper.Apply(ArpGrid, layouts.GetValueOrDefault("DeviceDetail.Arp"));
        DataGridLayoutHelper.Apply(AlertHistoryGrid, layouts.GetValueOrDefault("DeviceDetail.AlertHistory"));
        DataGridLayoutHelper.Apply(EventLogGrid, layouts.GetValueOrDefault("DeviceDetail.EventLog"));
    }

    /// <summary>
    /// Captures each sub-table's current layout - mirrors MainWindow's own
    /// SavePlacement, called at the same lifecycle point (closing). Only
    /// whichever section is actually selected when the window closes has a
    /// real layout to capture (every other section's DataGrid is
    /// Visibility=Collapsed at that moment) - DataGridLayoutHelper.Capture
    /// returns null for those, which is skipped, leaving that section's
    /// previously saved layout untouched rather than overwriting it with
    /// garbage read from an unrendered grid.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        var layouts = _settings.Current.GridLayouts;

        SetIfCaptured(layouts, "DeviceDetail.Ports", DataGridLayoutHelper.Capture(PortsGrid));
        SetIfCaptured(layouts, "DeviceDetail.Vlans", DataGridLayoutHelper.Capture(VlansGrid));
        SetIfCaptured(layouts, "DeviceDetail.Fdb", DataGridLayoutHelper.Capture(FdbGrid));
        SetIfCaptured(layouts, "DeviceDetail.Arp", DataGridLayoutHelper.Capture(ArpGrid));
        SetIfCaptured(layouts, "DeviceDetail.AlertHistory", DataGridLayoutHelper.Capture(AlertHistoryGrid));
        SetIfCaptured(layouts, "DeviceDetail.EventLog", DataGridLayoutHelper.Capture(EventLogGrid));

        _settings.Save();

        base.OnClosing(e);
    }

    private static void SetIfCaptured(Dictionary<string, GridLayout> layouts, string key, GridLayout? captured)
    {
        if (captured is not null)
        {
            layouts[key] = captured;
        }
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

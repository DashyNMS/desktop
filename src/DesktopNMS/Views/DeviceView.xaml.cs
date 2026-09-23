using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
        viewModel.ScrollToConfigLineRequested += OnScrollToConfigLineRequested;
        DiffRuler.NavigateRequested += OnDiffRulerNavigateRequested;

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
        if (e.PropertyName == nameof(DeviceDetailViewModel.ConfigLines))
        {
            // New content starts at the top unless the view model asks for
            // something else straight after (a change to jump to, or "stay
            // put" when a hidden run was expanded) - see ScheduleConfigLinesScroll.
            _configLinesPreviousOffset = GetConfigLinesScrollViewer()?.VerticalOffset ?? 0;
            ScheduleConfigLinesScroll(0);
        }

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

    private void OnExportEventLogButtonClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// The Unimus backups grid's own row selection drives the whole view/diff
    /// area - no checkbox column or separate buttons (issue #115 follow-up:
    /// the checkbox-based selection was fiddly). One row selected views it,
    /// two diffs them - see DeviceDetailViewModel.OnConfigSelectionChanged.
    /// </summary>
    private void OnUnimusBackupsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressUnimusSelectionForward)
        {
            ForwardUnimusSelection();
        }
    }

    private void ForwardUnimusSelection()
    {
        if (DataContext is DeviceDetailViewModel viewModel)
        {
            viewModel.OnConfigSelectionChanged(UnimusBackupsGrid.SelectedItems.Cast<UnimusBackupItemViewModel>().ToList());
        }
    }

    /// <summary>Set while a tick-box click is being applied, so the grid's own intermediate selection changes don't reach the view model.</summary>
    private bool _suppressUnimusSelectionForward;

    /// <summary>
    /// A tick-box click toggles that row in or out of the selection, like
    /// ctrl-click. This can't just be a two-way binding to the row's
    /// IsSelected: DataGridCell selects its row on mouse-down with a class
    /// handler that runs even for already-handled events, so the row was
    /// single-selected on press and then the tick box toggled it straight
    /// back off on release. Instead, the intended selection is worked out
    /// here, before the cell sees the click, and applied once the grid has
    /// finished its own handling - at Normal priority, ahead of rendering, so
    /// the grid's interim single-selection is never drawn.
    /// </summary>
    private void OnUnimusTickPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UnimusBackupItemViewModel item })
        {
            return;
        }

        e.Handled = true;

        var grid = UnimusBackupsGrid;
        var desired = grid.SelectedItems.Cast<UnimusBackupItemViewModel>().ToList();
        if (!desired.Remove(item))
        {
            desired.Add(item);
        }

        _suppressUnimusSelectionForward = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            // The cell's own mouse-down may have started a drag-select
            // (capturing the mouse) - end it so moving the mouse before
            // release doesn't re-select over the top of this.
            if (grid.IsMouseCaptured)
            {
                grid.ReleaseMouseCapture();
            }

            grid.SelectedItems.Clear();
            foreach (var backup in desired)
            {
                grid.SelectedItems.Add(backup);
            }

            _suppressUnimusSelectionForward = false;
            ForwardUnimusSelection();
        });
    }

    /// <summary>How many rows to leave above a change when jumping to it, so it doesn't sit flush against the top edge.</summary>
    private const int ConfigLinesJumpContext = 3;

    private double _configLinesPreviousOffset;
    private double? _pendingConfigLinesOffset;
    private ScrollViewer? _configLinesScroll;

    /// <summary>
    /// The line viewer's ScrollViewer lives in its ItemsControl's template,
    /// which isn't applied until the Unimus tab is first shown - so this is
    /// looked up lazily, and hooked for the overview ruler's viewport box the
    /// first time it's found.
    /// </summary>
    private ScrollViewer? GetConfigLinesScrollViewer()
    {
        if (_configLinesScroll is null)
        {
            ConfigLinesList.ApplyTemplate();
            _configLinesScroll = ConfigLinesList.Template?.FindName("LinesScroll", ConfigLinesList) as ScrollViewer;
            if (_configLinesScroll is not null)
            {
                _configLinesScroll.ScrollChanged += OnConfigLinesScrollChanged;
            }
        }

        return _configLinesScroll;
    }

    private void OnScrollToConfigLineRequested(int? row)
    {
        ScheduleConfigLinesScroll(row is { } r ? Math.Max(0, r - ConfigLinesJumpContext) : _configLinesPreviousOffset);
    }

    /// <summary>
    /// Scrolls once the new rows have actually been laid out (Loaded
    /// priority) - scrolling straight away would act on the old ItemsSource.
    /// Several requests in one pass collapse into one, the last one winning,
    /// so a content change's "back to the top" is overridden by a jump to
    /// the first change that immediately follows it.
    /// </summary>
    private void ScheduleConfigLinesScroll(double offset)
    {
        var alreadyScheduled = _pendingConfigLinesOffset is not null;
        _pendingConfigLinesOffset = offset;

        if (alreadyScheduled)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_pendingConfigLinesOffset is { } target && GetConfigLinesScrollViewer() is { } scroll)
            {
                scroll.ScrollToVerticalOffset(target);
                scroll.ScrollToHorizontalOffset(0);
            }

            _pendingConfigLinesOffset = null;
        });
    }

    private void OnConfigLinesScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Item-based scrolling - offset/viewport/extent are all in rows here.
        if (e.ExtentHeight > 0)
        {
            DiffRuler.ViewportStart = e.VerticalOffset / e.ExtentHeight;
            DiffRuler.ViewportSize = e.ViewportHeight / e.ExtentHeight;
        }
    }

    private void OnDiffRulerNavigateRequested(object? sender, double fraction)
    {
        if (GetConfigLinesScrollViewer() is { } scroll)
        {
            // Centre the clicked point rather than putting it at the top edge.
            scroll.ScrollToVerticalOffset(Math.Max(0, fraction * scroll.ExtentHeight - scroll.ViewportHeight / 2));
        }
    }

    /// <summary>A collapsed "N unchanged lines hidden" row expands when clicked; every other row ignores the click.</summary>
    private void OnConfigLineClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UnimusDiffLineViewModel { IsHidden: true } line }
            && DataContext is DeviceDetailViewModel viewModel)
        {
            viewModel.ExpandHiddenLinesCommand.Execute(line);
        }
    }
}

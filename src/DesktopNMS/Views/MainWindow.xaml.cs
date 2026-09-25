using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopNMS.Core.Configuration;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsStore _settings;

    /// <summary>
    /// Delays closing the Devices hover flyout (see #118) so moving the
    /// mouse from the Devices button down into the flyout, across the small
    /// gap between them, does not flicker it shut - both the button and the
    /// flyout's own content restart/cancel this same timer on
    /// MouseEnter/Leave.
    /// </summary>
    private readonly DispatcherTimer _devicesFlyoutCloseTimer;

    /// <summary>Same debounced hover-flyout behaviour as <see cref="_devicesFlyoutCloseTimer"/>, for Rules/Templates under the Alerts button.</summary>
    private readonly DispatcherTimer _alertsFlyoutCloseTimer;

    /// <summary>Same again, for Network/Geographical/Custom Maps under the Maps button.</summary>
    private readonly DispatcherTimer _mapsFlyoutCloseTimer;

    /// <summary>Same again, for the user's views under the Neighbours button.</summary>
    private readonly DispatcherTimer _neighboursFlyoutCloseTimer;

    private bool _allowClose;

    public MainWindow(MainViewModel viewModel, ISettingsStore settings)
    {
        _viewModel = viewModel;
        _settings = settings;

        InitializeComponent();

        DataContext = viewModel;

        _devicesFlyoutCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _devicesFlyoutCloseTimer.Tick += (_, _) =>
        {
            _devicesFlyoutCloseTimer.Stop();
            DevicesFlyout.IsOpen = false;
        };
        DevicesFlyout.PlacementTarget = DevicesTabButton;

        _alertsFlyoutCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _alertsFlyoutCloseTimer.Tick += (_, _) =>
        {
            _alertsFlyoutCloseTimer.Stop();
            AlertsFlyout.IsOpen = false;
        };
        AlertsFlyout.PlacementTarget = AlertsTabButton;

        _mapsFlyoutCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _mapsFlyoutCloseTimer.Tick += (_, _) =>
        {
            _mapsFlyoutCloseTimer.Stop();
            MapsFlyout.IsOpen = false;
        };
        MapsFlyout.PlacementTarget = MapsTabButton;

        _neighboursFlyoutCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _neighboursFlyoutCloseTimer.Tick += (_, _) =>
        {
            _neighboursFlyoutCloseTimer.Stop();
            NeighboursFlyout.IsOpen = false;
        };
        NeighboursFlyout.PlacementTarget = NeighboursTabButton;

        // Hidden to the tray or minimised: nothing on screen to keep live.
        IsVisibleChanged += (_, _) => NotifyVisibility();
        StateChanged += (_, _) => NotifyVisibility();

        RestorePlacement();
        ApplyGridLayouts();
    }

    private void DevicesTabButton_MouseEnter(object sender, MouseEventArgs e)
    {
        _devicesFlyoutCloseTimer.Stop();
        DevicesFlyout.IsOpen = true;
    }

    private void DevicesTabButton_MouseLeave(object sender, MouseEventArgs e) => _devicesFlyoutCloseTimer.Start();

    private void DevicesFlyoutContent_MouseEnter(object sender, MouseEventArgs e) => _devicesFlyoutCloseTimer.Stop();

    private void DevicesFlyoutContent_MouseLeave(object sender, MouseEventArgs e) => _devicesFlyoutCloseTimer.Start();

    private void DevicesFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        _devicesFlyoutCloseTimer.Stop();
        DevicesFlyout.IsOpen = false;
    }

    private void AlertsTabButton_MouseEnter(object sender, MouseEventArgs e)
    {
        _alertsFlyoutCloseTimer.Stop();
        AlertsFlyout.IsOpen = true;
    }

    private void AlertsTabButton_MouseLeave(object sender, MouseEventArgs e) => _alertsFlyoutCloseTimer.Start();

    private void AlertsFlyoutContent_MouseEnter(object sender, MouseEventArgs e) => _alertsFlyoutCloseTimer.Stop();

    private void AlertsFlyoutContent_MouseLeave(object sender, MouseEventArgs e) => _alertsFlyoutCloseTimer.Start();

    private void AlertsFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        _alertsFlyoutCloseTimer.Stop();
        AlertsFlyout.IsOpen = false;
    }

    private void MapsTabButton_MouseEnter(object sender, MouseEventArgs e)
    {
        _mapsFlyoutCloseTimer.Stop();
        MapsFlyout.IsOpen = true;
    }

    private void MapsTabButton_MouseLeave(object sender, MouseEventArgs e) => _mapsFlyoutCloseTimer.Start();

    private void MapsFlyoutContent_MouseEnter(object sender, MouseEventArgs e) => _mapsFlyoutCloseTimer.Stop();

    private void MapsFlyoutContent_MouseLeave(object sender, MouseEventArgs e) => _mapsFlyoutCloseTimer.Start();

    private void MapsFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        _mapsFlyoutCloseTimer.Stop();
        MapsFlyout.IsOpen = false;
    }

    private void NeighboursTabButton_MouseEnter(object sender, MouseEventArgs e)
    {
        _neighboursFlyoutCloseTimer.Stop();
        NeighboursFlyout.IsOpen = true;
    }

    private void NeighboursTabButton_MouseLeave(object sender, MouseEventArgs e) => _neighboursFlyoutCloseTimer.Start();

    private void NeighboursFlyoutContent_MouseEnter(object sender, MouseEventArgs e) => _neighboursFlyoutCloseTimer.Stop();

    private void NeighboursFlyoutContent_MouseLeave(object sender, MouseEventArgs e) => _neighboursFlyoutCloseTimer.Start();

    private void NeighboursFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        _neighboursFlyoutCloseTimer.Stop();
        NeighboursFlyout.IsOpen = false;
    }

    private void NotifyVisibility() =>
        _viewModel.OnWindowVisibilityChanged(IsVisible && WindowState != WindowState.Minimized);

    /// <summary>
    /// Lets the app close the window for real. Without this the Closing handler
    /// would keep hiding it to the tray and the process would never exit.
    /// </summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        SaveGridLayouts();

        if (!_allowClose && _settings.Current.MinimiseToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void OnFindExecuted(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (_viewModel.IsDevicesTabSelected)
        {
            DevicesViewControl.FocusSearch();
        }
        else if (_viewModel.IsHealthTabSelected)
        {
            HealthViewControl.FocusSearch();
        }
        else if (_viewModel.IsAlertsTabSelected)
        {
            AlertsViewControl.FocusSearch();
        }
        else if (_viewModel.IsGroupsTabSelected)
        {
            GroupsViewControl.FocusSearch();
        }
        else if (_viewModel.IsLocationsTabSelected)
        {
            LocationsViewControl.FocusSearch();
        }
        else if (_viewModel.IsNeighboursTabSelected)
        {
            NeighboursViewControl.FocusSearch();
        }
        else if (_viewModel.IsNetworkMapTabSelected)
        {
            NetworkMapViewControl.FocusSearch();
        }
        else if (_viewModel.IsGeoMapTabSelected)
        {
            GeoMapViewControl.FocusSearch();
        }
        else if (_viewModel.IsGraylogLogsTabSelected)
        {
            LogsViewControl.FocusSearch();
        }

        // Dashboard has no search box yet.
    }

    private void RestorePlacement()
    {
        var placement = _settings.Current.Window;
        if (placement is null || placement.Width < 400 || placement.Height < 300)
        {
            return;
        }

        // Ignore a saved position that is off-screen, which happens when a
        // monitor is unplugged between runs.
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var fitsHorizontally = placement.Left >= virtualLeft - 50 && placement.Left + 100 <= virtualRight;
        var fitsVertically = placement.Top >= virtualTop - 50 && placement.Top + 100 <= virtualBottom;

        Width = placement.Width;
        Height = placement.Height;

        if (fitsHorizontally && fitsVertically)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left;
            Top = placement.Top;
        }

        if (placement.Maximised)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        try
        {
            var maximised = WindowState == WindowState.Maximized;

            // RestoreBounds holds the normal-state geometry when maximised.
            var bounds = maximised ? RestoreBounds : new Rect(Left, Top, Width, Height);

            if (double.IsNaN(bounds.Width) || bounds.Width < 1)
            {
                return;
            }

            _settings.Current.Window = new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximised = maximised,
            };

            _settings.Save();
        }
        catch (Exception)
        {
            // Never let a placement problem block closing the window.
        }
    }

    /// <summary>Restores each MainWindow-hosted grid's remembered column widths/order/sort (issue #15) - see DataGridLayoutHelper.</summary>
    private void ApplyGridLayouts()
    {
        var layouts = _settings.Current.GridLayouts;

        DevicesViewControl.ApplyGridLayout(layouts.GetValueOrDefault("Devices"));
        AlertsViewControl.ApplyGridLayout(layouts.GetValueOrDefault("Alerts"));
        GroupsViewControl.ApplyGridLayout(layouts.GetValueOrDefault("Groups"));
        LocationsViewControl.ApplyGridLayout(layouts.GetValueOrDefault("Locations"));
        NeighboursViewControl.ApplyGridLayout(layouts.GetValueOrDefault("Neighbours"));
        HealthViewControl.ApplyGridLayout(layouts.GetValueOrDefault("Health.Sensors"));
    }

    /// <summary>
    /// Only whichever tab is actually showing when the window closes has a
    /// real layout to capture - every other tab's DataGrid is Visibility=Collapsed
    /// at that moment (WPF never lays out a collapsed element), so
    /// DataGridLayoutHelper.Capture returns null for it rather than the
    /// garbage its ActualWidth would otherwise read back as. A null result
    /// is skipped, leaving that tab's previously saved layout untouched.
    /// </summary>
    private void SaveGridLayouts()
    {
        try
        {
            var layouts = _settings.Current.GridLayouts;

            SetIfCaptured(layouts, "Devices", DevicesViewControl.CaptureGridLayout());
            SetIfCaptured(layouts, "Alerts", AlertsViewControl.CaptureGridLayout());
            SetIfCaptured(layouts, "Groups", GroupsViewControl.CaptureGridLayout());
            SetIfCaptured(layouts, "Locations", LocationsViewControl.CaptureGridLayout());
            SetIfCaptured(layouts, "Neighbours", NeighboursViewControl.CaptureGridLayout());
            SetIfCaptured(layouts, "Health.Sensors", HealthViewControl.CaptureGridLayout());

            _settings.Save();
        }
        catch (Exception)
        {
            // Never let a grid-layout problem block closing the window.
        }
    }

    private static void SetIfCaptured(Dictionary<string, GridLayout> layouts, string key, GridLayout? captured)
    {
        if (captured is not null)
        {
            layouts[key] = captured;
        }
    }
}

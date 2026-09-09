using System;
using System.ComponentModel;
using System.Windows;
using DesktopNMS.Core.Configuration;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsStore _settings;

    private bool _allowClose;

    public MainWindow(MainViewModel viewModel, ISettingsStore settings)
    {
        _viewModel = viewModel;
        _settings = settings;

        InitializeComponent();

        DataContext = viewModel;

        RestorePlacement();
    }

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
}

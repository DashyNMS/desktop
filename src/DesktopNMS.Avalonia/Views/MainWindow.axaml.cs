using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel, ISettingsStore settings)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        WindowPlacementMemory.Attach(this, settings);

        // Room for the OS's own window controls: macOS's traffic lights on the
        // left, Windows' buttons on the right. Linux window managers draw their
        // own title bar above this one.
        TitleBarContent.Margin = OperatingSystem.IsMacOS() ? new Thickness(80, 0, 12, 0)
            : OperatingSystem.IsWindows() ? new Thickness(19, 0, 146, 0)
            : new Thickness(19, 0, 12, 0);

        // Narrow, a section's sub-pages open in a flyout to its right on hover (#118).
        foreach (var section in new[] { DevicesSection, NeighboursSection, MapsSection, LogsSection, AlertsSection })
        {
            section.PointerEntered += (_, _) =>
            {
                if (_viewModel is { IsNavExpanded: false })
                {
                    FlyoutBase.ShowAttachedFlyout(section);
                }
            };
        }
    }

    /// <summary>The title bar moves the window, as the OS's own would.</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Button)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }
}

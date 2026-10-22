using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>Bulk add devices - see BulkAddDevicesViewModel.</summary>
public partial class BulkAddDevicesWindow : Window
{
    private readonly BulkAddDevicesViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public BulkAddDevicesWindow()
    {
        InitializeComponent();
    }

    public BulkAddDevicesWindow(BulkAddDevicesViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) => PasteBox.Focus();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Closing - by the button, Esc or the title bar - stops a run in progress;
    /// devices already being added finish on LibreNMS's side regardless.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _viewModel?.OnClosing();
        base.OnClosing(e);
    }
}

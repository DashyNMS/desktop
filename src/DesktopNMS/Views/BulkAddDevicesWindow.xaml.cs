using System.ComponentModel;
using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>Bulk add devices - see <see cref="BulkAddDevicesViewModel"/>.</summary>
public partial class BulkAddDevicesWindow : Window
{
    private readonly BulkAddDevicesViewModel _viewModel;

    public BulkAddDevicesWindow(BulkAddDevicesViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        Loaded += (_, _) => PasteBox.Focus();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Closing (by the button, Esc or the title bar) stops a run in progress
    /// - devices already being added finish on LibreNMS's side regardless.
    /// Whether anything was added is read from the view model afterwards
    /// (see WindowService.ShowBulkAddDevicesDialog), not DialogResult.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.OnClosing();
        base.OnClosing(e);
    }
}

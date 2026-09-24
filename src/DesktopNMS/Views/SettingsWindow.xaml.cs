using System.Windows;
using System.Windows.Controls;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
    }

    /// <summary>PasswordBox does not expose a bindable password, by design - see ConnectionWindow's identical pattern for the LibreNMS token.</summary>
    private void OnUnimusTokenChanged(object sender, RoutedEventArgs e)
        => _viewModel.UnimusTokenInput = ((PasswordBox)sender).Password;

    private void OnGraylogPasswordChanged(object sender, RoutedEventArgs e)
        => _viewModel.GraylogPasswordInput = ((PasswordBox)sender).Password;

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = saved;
    }
}

using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AddDeviceWindow : Window
{
    private readonly AddDeviceViewModel _viewModel;

    public AddDeviceWindow(AddDeviceViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;

        Loaded += (_, _) => HostnameBox.Focus();
    }

    /// <summary>
    /// PasswordBox does not expose a bindable password, by design, so the
    /// value is pushed to the view model here instead - same as
    /// ConnectionWindow's API token field.
    /// </summary>
    private void OnAuthPassChanged(object sender, RoutedEventArgs e)
        => _viewModel.AuthPass = AuthPassBox.Password;

    private void OnCryptoPassChanged(object sender, RoutedEventArgs e)
        => _viewModel.CryptoPass = CryptoPassBox.Password;

    private void OnRequestClose(object? sender, bool added)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = added;
    }
}

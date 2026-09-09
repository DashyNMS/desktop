using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class ConnectionWindow : Window
{
    private readonly ConnectionViewModel _viewModel;

    public ConnectionWindow(ConnectionViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;

        Loaded += (_, _) =>
        {
            // Land on whichever field is still empty.
            if (string.IsNullOrWhiteSpace(ServerBox.Text))
            {
                ServerBox.Focus();
            }
            else
            {
                TokenBox.Focus();
            }
        };
    }

    /// <summary>
    /// PasswordBox does not expose a bindable password, by design, so the value
    /// is pushed to the view model here instead.
    /// </summary>
    private void OnTokenChanged(object sender, RoutedEventArgs e)
        => _viewModel.ApiToken = TokenBox.Password;

    private void OnRequestClose(object? sender, bool success)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = success;
    }
}

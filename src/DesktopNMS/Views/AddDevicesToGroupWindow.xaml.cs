using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AddDevicesToGroupWindow : Window
{
    private readonly AddDevicesToGroupViewModel _viewModel;

    public AddDevicesToGroupWindow(AddDevicesToGroupViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = saved;
    }
}

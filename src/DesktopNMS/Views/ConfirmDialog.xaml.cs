using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class ConfirmDialog : Window
{
    private readonly ConfirmDialogViewModel _viewModel;

    public ConfirmDialog(ConfirmDialogViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, bool confirmed)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = confirmed;
    }
}

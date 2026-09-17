using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class LocationEditorWindow : Window
{
    private readonly LocationEditorViewModel _viewModel;

    public LocationEditorWindow(LocationEditorViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;

        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = saved;
    }
}

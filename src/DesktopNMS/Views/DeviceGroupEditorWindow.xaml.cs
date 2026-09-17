using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class DeviceGroupEditorWindow : Window
{
    private readonly DeviceGroupEditorViewModel _viewModel;

    public DeviceGroupEditorWindow(DeviceGroupEditorViewModel viewModel)
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

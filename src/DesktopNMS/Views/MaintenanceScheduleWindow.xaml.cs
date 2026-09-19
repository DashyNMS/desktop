using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class MaintenanceScheduleWindow : Window
{
    private readonly MaintenanceScheduleViewModel _viewModel;

    public MaintenanceScheduleWindow(MaintenanceScheduleViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;

        Loaded += (_, _) => TitleBox.Focus();
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = saved;
    }
}

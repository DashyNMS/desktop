using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class MaintenanceScheduleWindow : Window
{
    private readonly MaintenanceScheduleViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public MaintenanceScheduleWindow()
    {
        InitializeComponent();
    }

    public MaintenanceScheduleWindow(MaintenanceScheduleViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Opened += (_, _) => TitleBox.Focus();
    }

    /// <summary>True once scheduled.</summary>
    public bool Saved { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Saved = saved;
        Close();
    }
}

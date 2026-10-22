using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AddDevicesToGroupWindow : Window
{
    private readonly AddDevicesToGroupViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public AddDevicesToGroupWindow()
    {
        InitializeComponent();
    }

    public AddDevicesToGroupWindow(AddDevicesToGroupViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
    }

    /// <summary>True once the devices were added.</summary>
    public bool Saved { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Saved = saved;
        Close();
    }
}

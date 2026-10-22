using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class LocationEditorWindow : Window
{
    private readonly LocationEditorViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public LocationEditorWindow()
    {
        InitializeComponent();
    }

    public LocationEditorWindow(LocationEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Opened += (_, _) => NameBox.Focus();
    }

    /// <summary>True once the location was saved.</summary>
    public bool Saved { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Saved = saved;
        Close();
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Neighbours tab's New/Edit neighbourhood dialog - see <see cref="NeighbourViewEditorViewModel"/>.</summary>
public partial class NeighbourViewEditorWindow : Window
{
    private readonly NeighbourViewEditorViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public NeighbourViewEditorWindow()
    {
        InitializeComponent();
    }

    public NeighbourViewEditorWindow(NeighbourViewEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Opened += (_, _) => NameBox.Focus();
    }

    /// <summary>True once the neighbourhood was saved.</summary>
    public bool Saved { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Saved = saved;
        Close();
    }
}

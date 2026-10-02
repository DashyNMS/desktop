using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Neighbours tab's New/Edit view dialog - see <see cref="NeighbourViewEditorViewModel"/>.</summary>
public partial class NeighbourViewEditorWindow : Window
{
    private readonly NeighbourViewEditorViewModel _viewModel;

    public NeighbourViewEditorWindow(NeighbourViewEditorViewModel viewModel)
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

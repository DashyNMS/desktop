using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class RuleEditorWindow : Window
{
    private readonly RuleEditorViewModel _viewModel;

    public RuleEditorWindow(RuleEditorViewModel viewModel)
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

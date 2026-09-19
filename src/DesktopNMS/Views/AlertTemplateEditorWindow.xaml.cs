using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AlertTemplateEditorWindow : Window
{
    private readonly AlertTemplateEditorViewModel _viewModel;

    public AlertTemplateEditorWindow(AlertTemplateEditorViewModel viewModel)
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

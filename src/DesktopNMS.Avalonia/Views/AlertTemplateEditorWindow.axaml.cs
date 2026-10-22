using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AlertTemplateEditorWindow : Window
{
    private readonly AlertTemplateEditorViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public AlertTemplateEditorWindow()
    {
        InitializeComponent();
    }

    public AlertTemplateEditorWindow(AlertTemplateEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Opened += (_, _) => NameBox.Focus();
    }

    /// <summary>True once saved; closing any other way changes nothing.</summary>
    public bool Saved { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Saved = saved;
        Close();
    }
}

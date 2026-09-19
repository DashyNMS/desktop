using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>
    /// Drops the field picker's list open as soon as the user starts typing.
    /// StaysOpenOnEdit only keeps an already-open dropdown open, so without
    /// this the filtering happens invisibly behind a closed popup.
    /// </summary>
    private void OnFieldPickerPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is ComboBox picker)
        {
            picker.IsDropDownOpen = true;
        }
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = saved;
    }
}

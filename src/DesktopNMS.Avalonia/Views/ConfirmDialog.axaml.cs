using Avalonia.Controls;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class ConfirmDialog : Window
{
    private readonly ConfirmDialogViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(ConfirmDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;

        // The safe choice has focus: Cancel for a destructive action, else the confirm button.
        Opened += (_, _) => (viewModel.IsDestructive && viewModel.ShowCancel ? CancelButton : ConfirmButton).Focus();
    }

    /// <summary>True once the confirm button was chosen; closing any other way is a no.</summary>
    public bool Confirmed { get; private set; }

    private void OnRequestClose(object? sender, bool confirmed)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Confirmed = confirmed;
        Close();
    }
}

using System;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the themed dialog shown by <see cref="Services.IWindowService.Confirm"/>,
/// <see cref="Services.IWindowService.ConfirmWithOptOut"/>, <see cref="Services.IWindowService.ShowInformation"/>
/// and <see cref="Services.IWindowService.ShowError"/> - in place of a plain
/// <see cref="System.Windows.MessageBox"/>, which does not pick up the app's
/// own dark/light theme and stands out against the rest of the UI. The
/// Cancel button and "confirm" wording only make sense for the first two;
/// <see cref="ShowCancel"/>/<see cref="ConfirmLabel"/> adapt it for a plain
/// OK-only notice instead.
/// </summary>
public sealed class ConfirmDialogViewModel : ObservableObject
{
    public ConfirmDialogViewModel(
        string title,
        string message,
        bool showDontAskAgain,
        string dontAskAgainLabel,
        bool showCancel = true,
        string confirmLabel = "Confirm",
        bool isError = false)
    {
        Title = title;
        Message = message;
        ShowDontAskAgain = showDontAskAgain;
        DontAskAgainLabel = dontAskAgainLabel;
        ShowCancel = showCancel;
        ConfirmLabel = confirmLabel;
        IsError = isError;

        ConfirmCommand = new RelayCommand(() => RequestClose?.Invoke(this, true));
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
    }

    /// <summary>True (confirmed) or false (cancelled) - the code-behind maps this straight onto <see cref="System.Windows.Window.DialogResult"/>.</summary>
    public event EventHandler<bool>? RequestClose;

    public string Title { get; }

    public string Message { get; }

    public bool ShowDontAskAgain { get; }

    public string DontAskAgainLabel { get; }

    /// <summary>False for a plain OK-only notice (see <see cref="Services.IWindowService.ShowInformation"/>/<see cref="Services.IWindowService.ShowError"/>) - no Cancel button, and the Confirm button becomes the Escape/Cancel target too.</summary>
    public bool ShowCancel { get; }

    public string ConfirmLabel { get; }

    /// <summary>Tints the title red for an error notice - same red the rest of the app already uses for error banners/text.</summary>
    public bool IsError { get; }

    /// <summary>
    /// Read by the caller after the dialog closes, regardless of whether it
    /// was confirmed or cancelled - checking the box is a standalone "stop
    /// asking me" declaration, not conditional on this one answer.
    /// </summary>
    public bool DontAskAgain { get; set; }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }
}

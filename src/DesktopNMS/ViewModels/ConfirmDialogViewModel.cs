using System;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the themed confirmation dialog shown by <see cref="Services.IWindowService.Confirm"/>
/// and <see cref="Services.IWindowService.ConfirmWithOptOut"/>, in place of a
/// plain <see cref="System.Windows.MessageBox"/> that does not pick up the
/// app's own dark/light theme.
/// </summary>
public sealed class ConfirmDialogViewModel : ObservableObject
{
    public ConfirmDialogViewModel(string title, string message, bool showDontAskAgain, string dontAskAgainLabel)
    {
        Title = title;
        Message = message;
        ShowDontAskAgain = showDontAskAgain;
        DontAskAgainLabel = dontAskAgainLabel;

        ConfirmCommand = new RelayCommand(() => RequestClose?.Invoke(this, true));
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
    }

    /// <summary>True (confirmed) or false (cancelled) - the code-behind maps this straight onto <see cref="System.Windows.Window.DialogResult"/>.</summary>
    public event EventHandler<bool>? RequestClose;

    public string Title { get; }

    public string Message { get; }

    public bool ShowDontAskAgain { get; }

    public string DontAskAgainLabel { get; }

    /// <summary>
    /// Read by the caller after the dialog closes, regardless of whether it
    /// was confirmed or cancelled - checking the box is a standalone "stop
    /// asking me" declaration, not conditional on this one answer.
    /// </summary>
    public bool DontAskAgain { get; set; }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }
}

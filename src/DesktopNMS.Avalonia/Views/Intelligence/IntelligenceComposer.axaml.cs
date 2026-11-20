using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Intelligence;

/// <summary>The Intelligence composer (#327): the question box, model, detail and Ask/Stop.</summary>
public partial class IntelligenceComposer : UserControl
{
    public IntelligenceComposer()
    {
        InitializeComponent();

        // Tunnelling, so Enter is seen before the box does anything with it.
        QuestionBox.AddHandler(KeyDownEvent, OnQuestionKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret at the end of the question - after Edit question, or F6 to the conversation.</summary>
    public void FocusQuestion()
    {
        QuestionBox.Focus();
        QuestionBox.CaretIndex = QuestionBox.Text?.Length ?? 0;
    }

    /// <summary>The picker opens on the chat's own provider, with the search cleared.</summary>
    private void OnPickerOpening(object? sender, System.EventArgs e) => (DataContext as IntelligenceComposerViewModel)?.ResetPicker();

    /// <summary>
    /// A model, a reasoning level or Provider settings was chosen. A model with
    /// reasoning levels keeps the picker open so the level can be chosen next;
    /// anything else closes it. Closed afterwards, not here: Click comes before
    /// the button's command, and closing first would detach the command's binding.
    /// </summary>
    private void OnModelChosen(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: IntelligenceModelOption { HasEfforts: true } }) return;
        Dispatcher.UIThread.Post(() => ModelPickerButton.Flyout?.Hide());
    }

    /// <summary>Enter asks; Shift+Enter is a new line.</summary>
    private void OnQuestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var text = QuestionBox.Text ?? string.Empty;
            var caret = QuestionBox.CaretIndex;
            QuestionBox.Text = text.Insert(caret, "\n");
            QuestionBox.CaretIndex = caret + 1;
        }
        else if (DataContext is IntelligenceComposerViewModel composer && composer.SendCommand.CanExecute(null))
        {
            composer.SendCommand.Execute(null);
        }

        e.Handled = true;
    }
}

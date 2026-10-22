using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

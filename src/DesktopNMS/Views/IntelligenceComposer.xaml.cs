using System.Windows.Controls;
using System.Windows.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Intelligence composer (#327): the question box, model, detail and Ask/Stop.</summary>
public partial class IntelligenceComposer : UserControl
{
    public IntelligenceComposer()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret at the end of the question - after Edit question, or F6 to the conversation.</summary>
    public void FocusQuestion()
    {
        QuestionBox.Focus();
        QuestionBox.CaretIndex = QuestionBox.Text.Length;
    }

    /// <summary>Enter asks; Shift+Enter is a new line.</summary>
    private void OnQuestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var box = (TextBox)sender;
            var caret = box.CaretIndex;
            box.Text = box.Text.Insert(caret, "\n");
            box.CaretIndex = caret + 1;
        }
        else if (DataContext is IntelligenceComposerViewModel composer && composer.SendCommand.CanExecute(null))
        {
            composer.SendCommand.Execute(null);
        }

        e.Handled = true;
    }
}

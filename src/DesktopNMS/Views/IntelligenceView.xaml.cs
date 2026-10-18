using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class IntelligenceView : UserControl
{
    public IntelligenceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is IntelligenceViewModel old)
        {
            old.Messages.CollectionChanged -= OnMessagesChanged;
        }

        if (e.NewValue is IntelligenceViewModel model)
        {
            model.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    /// <summary>Follow the conversation as it grows, including the answer as it streams in.</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is INotifyPropertyChanged message)
                {
                    message.PropertyChanged += (_, _) => Transcript.ScrollToEnd();
                }
            }
        }

        Transcript.ScrollToEnd();
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
        else if (DataContext is IntelligenceViewModel model && model.SendCommand.CanExecute(null))
        {
            model.SendCommand.Execute(null);
        }

        e.Handled = true;
    }
}

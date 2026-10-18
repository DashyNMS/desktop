using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class IntelligenceView : UserControl
{
    /// <summary>
    /// Copy from an answer as plain text: the rich copy would carry the
    /// app's colours (white text, dark fills) into whatever it's pasted in.
    /// </summary>
    public static readonly DependencyProperty CopiesPlainTextProperty = DependencyProperty.RegisterAttached(
        "CopiesPlainText",
        typeof(bool),
        typeof(IntelligenceView),
        new PropertyMetadata(false, OnCopiesPlainTextChanged));

    public static bool GetCopiesPlainText(DependencyObject element) => (bool)element.GetValue(CopiesPlainTextProperty);

    public static void SetCopiesPlainText(DependencyObject element, bool value) => element.SetValue(CopiesPlainTextProperty, value);

    private static void OnCopiesPlainTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBox box && e.NewValue is true)
        {
            box.CommandBindings.Add(new CommandBinding(
                ApplicationCommands.Copy,
                (_, args) =>
                {
                    var text = box.Selection.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        Clipboard.SetText(text.Trim());
                    }

                    args.Handled = true;
                },
                (_, args) =>
                {
                    args.CanExecute = !box.Selection.IsEmpty;
                    args.Handled = true;
                }));
        }
    }

    /// <summary>A graph at full size, in a window of its own.</summary>
    private void OnGraphClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ImageSource image })
        {
            return;
        }

        var window = new Window
        {
            Title = "Graph",
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.CanResize,
            Background = Brushes.White,
            Content = new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(12),
                MaxWidth = SystemParameters.WorkArea.Width * 0.9,
                MaxHeight = SystemParameters.WorkArea.Height * 0.85,
            },
        };
        window.Show();
    }

    /// <summary>The whole answer, as the Markdown it came in - tables and lists intact.</summary>
    private void OnCopyAnswerClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: IntelligenceMessageViewModel message } } }
            && !string.IsNullOrWhiteSpace(message.Text))
        {
            Clipboard.SetText(message.Text.Trim());
        }
    }

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

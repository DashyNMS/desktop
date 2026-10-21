using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// The Intelligence conversation (#327): each question and answer, following
/// the latest as it streams in unless the person has scrolled up to read.
/// </summary>
public partial class IntelligenceTranscript : UserControl
{
    private readonly HashSet<IntelligenceMessageViewModel> _observedMessages = new();
    private IntelligenceViewModel? _model;
    private bool _followLatest = true;
    private bool _scrollPending;

    public IntelligenceTranscript()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Copy from an answer as plain text: the rich copy would carry the
    /// app's colours (white text, dark fills) into whatever it's pasted in.
    /// </summary>
    public static readonly DependencyProperty CopiesPlainTextProperty = DependencyProperty.RegisterAttached(
        "CopiesPlainText",
        typeof(bool),
        typeof(IntelligenceTranscript),
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

    private void OnSourceNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (WebSearchCitations.TryGetLink(e.Uri?.AbsoluteUri, out var uri))
        {
            _model?.OpenSource(uri);
        }
    }

    /// <summary>A graph at full size, in a window of its own.</summary>
    private void OnGraphClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: byte[] data } || Converters.ImageBytesToSourceConverter.Decode(data) is not { } image)
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
        window.Closed += (_, _) => { if (sender is Button { IsLoaded: true } button) button.Focus(); };
        window.Show();
    }

    /// <summary>The whole answer, as the Markdown it came in - tables and lists intact.</summary>
    private void OnCopyAnswerClick(object sender, RoutedEventArgs e)
    {
        var message = sender switch
        {
            FrameworkElement { DataContext: IntelligenceMessageViewModel direct } => direct,
            MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: IntelligenceMessageViewModel context } } } => context,
            _ => null,
        };
        if (message?.CanCopyAnswer == true)
        {
            Clipboard.SetText(message.CopyMarkdown);
            message.MarkCopied();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _model = e.NewValue as IntelligenceViewModel;
        if (_model is not null)
        {
            _model.Messages.CollectionChanged += OnMessagesChanged;
        }

        _followLatest = true;
        SynchronizeMessageSubscriptions();
        ScheduleScrollToLatest();
    }

    /// <summary>Follow the conversation as it grows, including the answer as it streams in.</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SynchronizeMessageSubscriptions();
        if (e.Action == NotifyCollectionChangedAction.Reset || e.NewItems?.OfType<IntelligenceMessageViewModel>().Any(message => message.IsUser) == true) _followLatest = true;
        if (_followLatest) ScheduleScrollToLatest();
        else JumpToLatestButton.Visibility = Visibility.Visible;
    }

    private void SynchronizeMessageSubscriptions()
    {
        var messages = _model?.Messages.ToHashSet() ?? new HashSet<IntelligenceMessageViewModel>();
        foreach (var message in _observedMessages.Where(message => !messages.Contains(message)).ToArray())
        {
            message.PropertyChanged -= OnMessagePropertyChanged;
            _observedMessages.Remove(message);
        }
        foreach (var message in messages.Where(message => !_observedMessages.Contains(message)))
        {
            message.PropertyChanged += OnMessagePropertyChanged;
            _observedMessages.Add(message);
        }
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(IntelligenceMessageViewModel.Text) or nameof(IntelligenceMessageViewModel.HasImages))) return;
        if (_followLatest) ScheduleScrollToLatest();
        else JumpToLatestButton.Visibility = Visibility.Visible;
    }

    private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource != Transcript) return;
        if (e.ExtentHeightChange == 0 && e.VerticalChange != 0)
            _followLatest = Transcript.ScrollableHeight - Transcript.VerticalOffset <= 24;
        if (e.ExtentHeightChange != 0 && _followLatest) ScheduleScrollToLatest();
        JumpToLatestButton.Visibility = !_followLatest && Transcript.ScrollableHeight - Transcript.VerticalOffset > 24 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleScrollToLatest()
    {
        if (!_followLatest || _scrollPending) return;
        _scrollPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _scrollPending = false;
            if (_followLatest) { Transcript.ScrollToEnd(); JumpToLatestButton.Visibility = Visibility.Collapsed; }
        }, DispatcherPriority.Loaded);
    }

    private void OnJumpToLatestClick(object sender, RoutedEventArgs e)
    {
        _followLatest = true;
        ScheduleScrollToLatest();
    }
}

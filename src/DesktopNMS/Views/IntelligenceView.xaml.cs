using System.Collections.Specialized;
using System.ComponentModel;
using System;
using System.Collections.Generic;
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

public partial class IntelligenceView : UserControl
{
    private readonly HashSet<IntelligenceMessageViewModel> _observedMessages = new();
    private bool _followLatest = true;
    private bool _scrollPending;
    private bool _drawerOpen;
    private IInputElement? _drawerReturnFocus;
    private void OnSourceNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (WebSearchCitations.TryGetLink(e.Uri?.AbsoluteUri, out var uri)
            && DataContext is IntelligenceViewModel model)
        {
            model.OpenSource(uri);
        }
    }

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

    public IntelligenceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Narrower than this, the context sidebar steps aside so the conversation keeps its room.</summary>
    private const double SidebarMinWidth = 1000;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is IntelligenceViewModel old)
        {
            old.Messages.CollectionChanged -= OnMessagesChanged;
            old.PropertyChanged -= OnModelPropertyChanged;
            old.QuestionFocusRequested -= OnQuestionFocusRequested;
        }

        if (e.NewValue is IntelligenceViewModel model)
        {
            model.Messages.CollectionChanged += OnMessagesChanged;
            model.PropertyChanged += OnModelPropertyChanged;
            model.QuestionFocusRequested += OnQuestionFocusRequested;
        }
        _followLatest = true;
        SynchronizeMessageSubscriptions();
        UpdateSidebar();
        ScheduleScrollToLatest();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IntelligenceViewModel.IsSidebarOpen) or nameof(IntelligenceViewModel.ShowConversation))
        {
            UpdateSidebar();
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSidebar();

    private bool HasRoomForContext => Root.ActualWidth >= SidebarMinWidth + ChatSidebar.Width;

    private void OnShowContextClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not IntelligenceViewModel model) return;
        if (HasRoomForContext) model.IsSidebarOpen = true;
        else
        {
            _drawerReturnFocus = Keyboard.FocusedElement;
            _drawerOpen = true;
            UpdateSidebar();
            Sidebar.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    private void OnCloseContextClick(object sender, RoutedEventArgs e)
    {
        if (HasRoomForContext && DataContext is IntelligenceViewModel model) model.IsSidebarOpen = false;
        _drawerOpen = false;
        UpdateSidebar();
        if (_drawerReturnFocus is { } focus) Keyboard.Focus(focus);
        _drawerReturnFocus = null;
    }

    private void OnRootKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drawerOpen) { OnCloseContextClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.F6)
        {
            if (_drawerOpen)
            {
                Sidebar.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                e.Handled = true;
                return;
            }
            var panes = new FrameworkElement[] { ChatSidebar, ConversationPane, Sidebar }.Where(pane => pane.IsVisible).ToArray();
            var current = Array.FindIndex(panes, pane => pane.IsKeyboardFocusWithin);
            var direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            if (panes.Length > 0)
            {
                var target = panes[(current + direction + panes.Length) % panes.Length];
                if (target == ConversationPane) QuestionBox.Focus();
                else if (!target.Focus()) target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                e.Handled = true;
            }
        }
    }

    private void OnQuestionFocusRequested(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() => { QuestionBox.Focus(); QuestionBox.CaretIndex = QuestionBox.Text.Length; });

    /// <summary>
    /// The sidebar shows when there's a conversation, it hasn't been
    /// collapsed, and the window is wide enough. Collapsed (with room for
    /// it), the top bar's Context button brings it back.
    /// </summary>
    private void UpdateSidebar()
    {
        if (Sidebar is null || DataContext is not IntelligenceViewModel model)
        {
            return;
        }

        ChatSidebar.Visibility = model.ShowConversation ? Visibility.Visible : Visibility.Collapsed;
        var room = model.ShowConversation && HasRoomForContext;
        var host = room ? DockedSidebarHost : ContextDrawer;
        if (Sidebar.Parent is Panel parent && parent != host)
        {
            parent.Children.Remove(Sidebar);
            host.Children.Add(Sidebar);
        }
        if (room) _drawerOpen = false;
        if (!model.ShowConversation) _drawerOpen = false;
        Sidebar.HorizontalAlignment = room ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Sidebar.Visibility = model.ShowConversation && (room ? model.IsSidebarOpen : _drawerOpen) ? Visibility.Visible : Visibility.Collapsed;
        ContextDrawer.Visibility = !room && _drawerOpen ? Visibility.Visible : Visibility.Collapsed;
        ShowContextButton.Visibility = model.ShowConversation && (!room || !model.IsSidebarOpen) ? Visibility.Visible : Visibility.Collapsed;
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
        var messages = DataContext is IntelligenceViewModel model ? model.Messages.ToHashSet() : new HashSet<IntelligenceMessageViewModel>();
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

    private void OnChatNameKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not IntelligenceViewModel model) return;
        if (e.Key == Key.Enter && model.SaveChatNameCommand.CanExecute(null))
        {
            model.SaveChatNameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            model.CancelChatNameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.InvokeAsync(() => { if (box.IsVisible) { box.Focus(); box.SelectAll(); } });
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

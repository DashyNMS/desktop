using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// The Intelligence tab: the chat list, the conversation and composer, and the
/// context sidebar, each its own control (#327). This lays them out - the
/// sidebar docks or becomes a drawer by width - and moves focus between them.
/// </summary>
public partial class IntelligenceView : UserControl
{
    /// <summary>Narrower than this, the context sidebar steps aside so the conversation keeps its room.</summary>
    private const double SidebarMinWidth = 1000;

    private bool _drawerOpen;
    private IInputElement? _drawerReturnFocus;

    public IntelligenceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is IntelligenceViewModel old)
        {
            old.PropertyChanged -= OnModelPropertyChanged;
            old.QuestionFocusRequested -= OnQuestionFocusRequested;
        }

        if (e.NewValue is IntelligenceViewModel model)
        {
            model.PropertyChanged += OnModelPropertyChanged;
            model.QuestionFocusRequested += OnQuestionFocusRequested;
        }

        UpdateSidebar();
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
                if (target == ConversationPane) ComposerBox.FocusQuestion();
                else if (!target.Focus()) target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                e.Handled = true;
            }
        }
    }

    private void OnQuestionFocusRequested(object? sender, EventArgs e) => Dispatcher.InvokeAsync(ComposerBox.FocusQuestion);

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
}

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Intelligence;

/// <summary>
/// The Intelligence tab (#250, from the WPF app's IntelligenceView): the chat
/// list, the conversation and composer, and the context sidebar, each its own
/// control (#327). This lays them out - the sidebar docks or becomes a drawer
/// by width - and moves focus between them (F6).
/// </summary>
public partial class IntelligenceView : UserControl
{
    /// <summary>Narrower than this, the context sidebar steps aside so the conversation keeps its room.</summary>
    private const double SidebarMinWidth = 1000;

    private IntelligenceViewModel? _model;
    private bool _drawerOpen;
    private IInputElement? _drawerReturnFocus;

    public IntelligenceView()
    {
        InitializeComponent();
        Root.SizeChanged += (_, _) => UpdateSidebar();
        AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.QuestionFocusRequested -= OnQuestionFocusRequested;
        }

        _model = DataContext as IntelligenceViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
            _model.QuestionFocusRequested += OnQuestionFocusRequested;
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

    private bool HasRoomForContext => Root.Bounds.Width >= SidebarMinWidth + ChatSidebar.Width;

    private void OnShowContextClick(object? sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        if (HasRoomForContext)
        {
            _model.IsSidebarOpen = true;
        }
        else
        {
            _drawerReturnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            _drawerOpen = true;
            UpdateSidebar();
            FocusFirstIn(Sidebar);
        }
    }

    private void OnCloseContextClick(object? sender, RoutedEventArgs e) => CloseContext();

    private void CloseContext()
    {
        if (HasRoomForContext && _model is not null)
        {
            _model.IsSidebarOpen = false;
        }

        _drawerOpen = false;
        UpdateSidebar();
        _drawerReturnFocus?.Focus();
        _drawerReturnFocus = null;
    }

    /// <summary>Escape closes the drawer; F6 and Shift+F6 move between the chats, the conversation and the context.</summary>
    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control && _model?.ClearCommand.CanExecute(null) == true)
        {
            _model.ClearCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _drawerOpen)
        {
            CloseContext();
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            if (_drawerOpen)
            {
                FocusFirstIn(Sidebar);
                e.Handled = true;
                return;
            }

            var panes = new Control[] { ChatSidebar, ConversationPane, Sidebar }.Where(pane => pane.IsEffectivelyVisible).ToArray();
            if (panes.Length == 0)
            {
                return;
            }

            var current = Array.FindIndex(panes, pane => pane.IsKeyboardFocusWithin);
            var direction = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            var target = panes[(current + direction + panes.Length) % panes.Length];
            if (target == ConversationPane)
            {
                ComposerBox.FocusQuestion();
            }
            else
            {
                FocusFirstIn(target);
            }

            e.Handled = true;
        }
    }

    private static void FocusFirstIn(Control pane) =>
        pane.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(element => element.Focusable && element.IsEffectivelyVisible && element.IsEffectivelyEnabled)
            ?.Focus(NavigationMethod.Tab);

    private void OnQuestionFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(ComposerBox.FocusQuestion, DispatcherPriority.Loaded);

    /// <summary>
    /// The sidebar shows when there's a conversation, it hasn't been
    /// collapsed, and the window is wide enough. Collapsed (with room for
    /// it), the top bar's Context button brings it back.
    /// </summary>
    private void UpdateSidebar()
    {
        if (_model is null)
        {
            return;
        }

        ChatSidebar.IsVisible = _model.ShowConversation;
        var room = _model.ShowConversation && HasRoomForContext;
        var host = room ? DockedSidebarHost : ContextDrawer;
        if (Sidebar.Parent is Panel parent && parent != host)
        {
            parent.Children.Remove(Sidebar);
            host.Children.Add(Sidebar);
        }

        if (room || !_model.ShowConversation)
        {
            _drawerOpen = false;
        }

        Sidebar.HorizontalAlignment = room ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Sidebar.IsVisible = _model.ShowConversation && (room ? _model.IsSidebarOpen : _drawerOpen);
        ContextDrawer.IsVisible = !room && _drawerOpen;
        ShowContextButton.IsVisible = _model.ShowConversation && (!room || !_model.IsSidebarOpen);
    }
}

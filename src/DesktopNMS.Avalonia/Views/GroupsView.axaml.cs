using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Groups tab - see <see cref="GroupsViewModel"/>.</summary>
public partial class GroupsView : UserControl
{
    public GroupsView()
    {
        InitializeComponent();

        // Tunnelling, so a shift-click on a type count is seen before it toggles.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Shift-clicking a type count shows only that type instead of toggling it.</summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Shift
            && DataContext is GroupsViewModel viewModel
            && (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is { Tag: string type } badge
            && badge.Classes.Contains("groupType"))
        {
            viewModel.IsolateType(isStatic: type == "static");
            e.Handled = true;
        }
    }
}

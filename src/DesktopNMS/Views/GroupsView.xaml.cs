using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class GroupsView : UserControl
{
    public GroupsView()
    {
        InitializeComponent();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Shift-clicking a type badge isolates that type instead of toggling it
    /// normally - same pattern as DevicesView's own status badges, handled
    /// here rather than a Command so the plain click still flips IsChecked
    /// via its binding unchanged.
    /// </summary>
    private void TypeBadge_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift)
        {
            return;
        }

        if (sender is not ToggleButton { Tag: string tag } || DataContext is not GroupsViewModel vm)
        {
            return;
        }

        vm.IsolateType(isStatic: tag == "static");
        e.Handled = true;
    }
}

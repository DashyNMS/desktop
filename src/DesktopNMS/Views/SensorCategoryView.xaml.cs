using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class SensorCategoryView : UserControl
{
    public SensorCategoryView()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in the search box. Called by the Health tab shell on Ctrl+F.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Shift-clicking a status badge isolates that status instead of toggling
    /// it normally - handled here (rather than a Command) so the plain click
    /// still flips <c>IsChecked</c> via its binding unchanged.
    /// </summary>
    private void SeverityBadge_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift)
        {
            return;
        }

        if (sender is not ToggleButton { Tag: AlertSeverity severity } || DataContext is not SensorCategoryViewModel vm)
        {
            return;
        }

        vm.IsolateSeverity(severity);
        e.Handled = true;
    }
}

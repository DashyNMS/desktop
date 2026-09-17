using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
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

    /// <summary>All four Health categories share one "Health.Sensors" layout key (see HealthView.xaml.cs) - their columns are identical, just filtered differently.</summary>
    public void ApplyGridLayout(GridLayout? layout) => DataGridLayoutHelper.Apply(SensorsGrid, layout);

    public GridLayout? CaptureGridLayout() => DataGridLayoutHelper.Capture(SensorsGrid);

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

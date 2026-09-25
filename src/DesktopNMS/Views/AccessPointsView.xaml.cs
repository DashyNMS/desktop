using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopNMS.ViewModels;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.Views;

/// <summary>The Access points page - see <see cref="ViewModels.AccessPointsViewModel"/>.</summary>
public partial class AccessPointsView : UserControl
{
    public AccessPointsView()
    {
        InitializeComponent();
    }

    private void StateBadge_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift
            || sender is not ToggleButton { Tag: AccessPointState state }
            || DataContext is not AccessPointsViewModel vm)
        {
            return;
        }

        vm.IsolateState(state);
        e.Handled = true;
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ApplyGridLayout(GridLayout? layout) => DataGridLayoutHelper.Apply(AccessPointsGrid, layout);

    public GridLayout? CaptureGridLayout() => DataGridLayoutHelper.Capture(AccessPointsGrid);
}

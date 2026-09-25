using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Neighbours tab - see <see cref="NeighboursViewModel"/>.</summary>
public partial class NeighboursView : UserControl
{
    public NeighboursView()
    {
        InitializeComponent();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ApplyGridLayout(GridLayout? layout) => DataGridLayoutHelper.Apply(NeighboursGrid, layout);

    public GridLayout? CaptureGridLayout() => DataGridLayoutHelper.Capture(NeighboursGrid);

    private void StateBadge_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift
            || sender is not ToggleButton { Tag: NeighbourState state }
            || DataContext is not NeighboursViewModel vm)
        {
            return;
        }

        vm.IsolateState(state);
        e.Handled = true;
    }
}

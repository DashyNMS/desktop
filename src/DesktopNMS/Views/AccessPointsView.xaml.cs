using System.Windows.Controls;
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

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ApplyGridLayout(GridLayout? layout) => DataGridLayoutHelper.Apply(AccessPointsGrid, layout);

    public GridLayout? CaptureGridLayout() => DataGridLayoutHelper.Capture(AccessPointsGrid);
}

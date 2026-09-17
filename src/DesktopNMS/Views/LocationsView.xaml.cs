using System.Windows.Controls;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.Views;

public partial class LocationsView : UserControl
{
    public LocationsView()
    {
        InitializeComponent();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ApplyGridLayout(GridLayout? layout) => DataGridLayoutHelper.Apply(LocationsGrid, layout);

    public GridLayout? CaptureGridLayout() => DataGridLayoutHelper.Capture(LocationsGrid);
}

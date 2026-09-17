using System.Windows.Controls;

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
}

using System.Windows.Controls;

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
}

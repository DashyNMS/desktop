using System.Windows.Controls;

namespace DesktopNMS.Views;

public partial class HealthView : UserControl
{
    public HealthView()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in the search box. Called by the shell window on Ctrl+F.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}

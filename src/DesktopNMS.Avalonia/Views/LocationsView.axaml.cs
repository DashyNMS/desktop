using Avalonia.Controls;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Locations tab - see <see cref="LocationsViewModel"/>.</summary>
public partial class LocationsView : UserControl
{
    public LocationsView()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}

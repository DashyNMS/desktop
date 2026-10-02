using System.Windows.Controls;

namespace DesktopNMS.Views;

/// <summary>The Logs tab - see <see cref="ViewModels.LogsViewModel"/>.</summary>
public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
    }

    /// <summary>Called by the shell window on Ctrl+F.</summary>
    public void FocusSearch() => GraylogView.FocusSearch();
}

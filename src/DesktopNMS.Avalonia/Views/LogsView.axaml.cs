using Avalonia.Controls;

namespace DesktopNMS.Views;

/// <summary>The Logs tab - see <see cref="ViewModels.LogsViewModel"/>.</summary>
public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in Graylog's search box (Ctrl+F).</summary>
    public void FocusSearch() => GraylogView.FocusSearch();
}

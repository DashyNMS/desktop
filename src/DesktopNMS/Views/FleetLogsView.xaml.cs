using System.Windows.Controls;
using System.Windows.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class FleetLogsView : UserControl
{
    public FleetLogsView()
    {
        InitializeComponent();
    }

    /// <summary>Double-clicking an entry opens that device's log in Device Details.</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.FrameworkElement { DataContext: FleetEventRow or FleetAlertRow }
            && DataContext is FleetLogsViewModel viewModel)
        {
            viewModel.OpenSelectedCommand.Execute(null);
        }
    }
}

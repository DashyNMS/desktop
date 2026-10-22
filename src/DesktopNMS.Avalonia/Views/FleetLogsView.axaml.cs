using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Logs tab's Event log and Alert log - see <see cref="FleetLogsViewModel"/>.</summary>
public partial class FleetLogsView : UserControl
{
    public FleetLogsView()
    {
        InitializeComponent();

        EventsGrid.DoubleTapped += OnRowDoubleTapped;
        AlertsGrid.DoubleTapped += OnRowDoubleTapped;
    }

    /// <summary>Double-clicking an entry opens that device's log in Device Details.</summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: FleetEventRow or FleetAlertRow }
            && DataContext is FleetLogsViewModel viewModel
            && viewModel.OpenSelectedCommand.CanExecute(null))
        {
            viewModel.OpenSelectedCommand.Execute(null);
        }
    }
}

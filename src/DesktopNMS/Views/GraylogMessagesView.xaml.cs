using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>Graylog messages - see <see cref="GraylogMessagesViewModel"/>.</summary>
public partial class GraylogMessagesView : UserControl
{
    public GraylogMessagesView()
    {
        InitializeComponent();
    }

    /// <summary>Double-clicking a message opens the device it came from, when it's a known one - only on the Logs tab, where it's a different device each row.</summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not GraylogMessagesViewModel { IsFleet: true } viewModel
            || FindRow(e.OriginalSource as DependencyObject)?.Item is not GraylogMessageItemViewModel item)
        {
            return;
        }

        if (viewModel.OpenDeviceCommand.CanExecute(item))
        {
            viewModel.OpenDeviceCommand.Execute(item);
            e.Handled = true;
        }
    }

    private static DataGridRow? FindRow(DependencyObject? source)
    {
        while (source is not null and not DataGridRow)
        {
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        return source as DataGridRow;
    }
}

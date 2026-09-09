using System.Windows.Controls;
using System.Windows.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in the search box. Called by the shell window on Ctrl+F.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DeviceListViewModel viewModel && viewModel.OpenDeviceCommand.CanExecute(null))
        {
            viewModel.OpenDeviceCommand.Execute(null);
        }
    }
}

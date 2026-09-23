using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AlertFiltersWindow : Window
{
    public AlertFiltersWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

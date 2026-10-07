using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>Settings › About › Open-source licences - see <see cref="LicencesViewModel"/>.</summary>
public partial class LicencesWindow : Window
{
    public LicencesWindow(LicencesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

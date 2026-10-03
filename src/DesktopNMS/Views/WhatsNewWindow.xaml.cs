using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>"What's new" for this release (#227) - see <see cref="WhatsNewViewModel"/>.</summary>
public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow(WhatsNewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

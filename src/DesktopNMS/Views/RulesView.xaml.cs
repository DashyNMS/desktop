using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class RulesView : UserControl
{
    public RulesView()
    {
        InitializeComponent();
    }

    /// <summary>Shift-click on a severity pill shows only that severity - the same gesture as the Alerts and Devices tabs.</summary>
    private void SeverityBadge_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift)
        {
            return;
        }

        if (sender is not ToggleButton { Tag: AlertSeverity severity } || DataContext is not RulesViewModel vm)
        {
            return;
        }

        vm.IsolateSeverity(severity);
        e.Handled = true;
    }
}

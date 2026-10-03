using System.Windows.Controls;

namespace DesktopNMS.Views;

/// <summary>The tray icon's right-click menu (#232) - see <see cref="Services.TrayIconService"/>.</summary>
public partial class TrayMenu : ContextMenu
{
    public TrayMenu()
    {
        InitializeComponent();
    }
}

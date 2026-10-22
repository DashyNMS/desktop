using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Room for the OS's window controls beside a window's own drawn title bar
/// (#250): macOS's traffic lights on the left; on Windows, the window buttons
/// Avalonia draws over the right-hand end, measured as they're laid out, so
/// the title bar's own buttons stay clear whatever their number and size.
/// Linux window managers draw their own title bar above this one.
/// </summary>
public static class TitleBarFit
{
    public static void Attach(Window window, Control content, double left = 16)
    {
        content.Margin = OperatingSystem.IsMacOS() ? new Thickness(80, 0, 12, 0)
            : OperatingSystem.IsWindows() ? new Thickness(left, 0, 146, 0)
            : new Thickness(left, 0, 12, 0);

        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        StackPanel? captionButtons = null;
        window.LayoutUpdated += (_, _) =>
        {
            captionButtons ??= window.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "PART_OverlayPanel");
            if (captionButtons is not { IsVisible: true } buttons)
            {
                return;
            }

            var right = buttons.Bounds.Width + 8;
            if (right > 8 && Math.Abs(content.Margin.Right - right) > 0.5)
            {
                var margin = content.Margin;
                content.Margin = new Thickness(margin.Left, margin.Top, right, margin.Bottom);
            }
        };
    }
}

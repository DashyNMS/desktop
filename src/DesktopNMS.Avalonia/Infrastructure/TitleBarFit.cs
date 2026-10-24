using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
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
    private const double Gap = 8;

    public static void Attach(Window window, Control content, double left = 16)
    {
        content.Margin = OperatingSystem.IsMacOS() ? new Thickness(80, 0, 12, 0)
            : OperatingSystem.IsWindows() ? new Thickness(left, 0, 146, 0)
            : new Thickness(left, 0, 12, 0);

        if (OperatingSystem.IsMacOS())
        {
            void FitTrafficLights() => content.Margin = new Thickness(
                window.WindowState == WindowState.FullScreen ? left : 80, 0, 12, 0);

            FitTrafficLights();
            window.PropertyChanged += (_, args) =>
            {
                if (args.Property == Window.WindowStateProperty) FitTrafficLights();
            };
            return;
        }

        // The buttons live in a layer beside the window's own content, under
        // the host the window sits in - not among the window's descendants.
        StackPanel? captionButtons = null;
        StackPanel? FindCaptionButtons() => captionButtons ??=
            (window.GetVisualParent() ?? window).GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "PART_OverlayPanel");

        // Avalonia 12.1 keeps the hidden fullscreen button's desired width
        // in this separately hosted panel. Remove that unused template child
        // so both measurement and arrangement use the same three buttons
        // (#406), including when the window opens already maximised.
        window.Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                if (FindCaptionButtons() is { } buttons
                    && buttons.Children.OfType<Button>().FirstOrDefault(b => b.Name == "PART_FullScreenButton") is { IsVisible: false } fullscreen)
                {
                    buttons.Children.Remove(fullscreen);
                    buttons.InvalidateMeasure();
                }
            }, DispatcherPriority.Loaded);

        window.LayoutUpdated += (_, _) =>
        {
            if (FindCaptionButtons() is not { IsVisible: true } buttons
                || buttons.Bounds.Width <= 0
                || buttons.TranslatePoint(default, window) is not { } origin)
            {
                return;
            }

            // From where the buttons actually start, not just how wide they
            // are: they aren't always flush with the window's right edge.
            var right = Math.Max(0, window.Bounds.Width - origin.X) + Gap;
            if (Math.Abs(content.Margin.Right - right) > 0.5)
            {
                var margin = content.Margin;
                content.Margin = new Thickness(margin.Left, margin.Top, right, margin.Bottom);
            }
        };

    }
}

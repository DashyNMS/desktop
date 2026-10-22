using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;

namespace DesktopNMS.Views;

/// <summary>
/// The tray icon's quick look (#232, #252): opens beside the tray on a left
/// click and closes when it loses focus, like the system's own flyouts.
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    /// <summary>The gap to the taskbar and the screen edge, in pixels at 100%.</summary>
    private const int Gap = 12;

    public TrayFlyoutWindow()
    {
        InitializeComponent();

        Deactivated += (_, _) => HideFlyout();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideFlyout();
            }
        };
    }

    /// <summary>When it last closed - a click on the tray icon closes it first, and mustn't then reopen it.</summary>
    public DateTime LastHidden { get; private set; } = DateTime.MinValue;

    public void HideFlyout()
    {
        if (IsVisible)
        {
            Hide();
            LastHidden = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Shows it in the corner of the screen the pointer is on (or the primary
    /// screen), against whichever edge the taskbar or panel is on: the work
    /// area is the screen less the taskbar, so the side it's missing from is
    /// the side the taskbar is on.
    /// </summary>
    public void ShowNear(PixelPoint? cursor)
    {
        // Shown first, off to one side, so it has its real size to place.
        Position = new PixelPoint(-10000, -10000);
        Show();

        var screen = (cursor is { } point ? Screens.ScreenFromPoint(point) : null) ?? Screens.Primary;
        if (screen is null)
        {
            Activate();
            return;
        }

        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        var gap = (int)Math.Round(Gap * scale);
        var at = cursor ?? new PixelPoint(work.Right, work.Bottom);

        int x;
        int y;
        if (work.Bottom < bounds.Bottom)
        {
            // Taskbar at the bottom (the usual place on Windows).
            x = Math.Clamp(at.X - (width / 2), work.X + gap, work.Right - width - gap);
            y = work.Bottom - height - gap;
        }
        else if (work.Y > bounds.Y)
        {
            // At the top - a GNOME or KDE panel, say.
            x = Math.Clamp(at.X - (width / 2), work.X + gap, work.Right - width - gap);
            y = work.Y + gap;
        }
        else if (work.X > bounds.X)
        {
            x = work.X + gap;
            y = Math.Clamp(at.Y - (height / 2), work.Y + gap, work.Bottom - height - gap);
        }
        else if (work.Right < bounds.Right)
        {
            x = work.Right - width - gap;
            y = Math.Clamp(at.Y - (height / 2), work.Y + gap, work.Bottom - height - gap);
        }
        else
        {
            // An auto-hidden taskbar takes no room: the bottom right corner.
            x = work.Right - width - gap;
            y = work.Bottom - height - gap;
        }

        Position = new PixelPoint(x, y);
        Activate();
    }
}

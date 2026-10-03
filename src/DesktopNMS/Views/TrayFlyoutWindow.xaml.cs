using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopNMS.Views;

/// <summary>
/// The tray icon's quick look (#232): opens beside the notification area on
/// a left click and closes when it loses focus, like Windows' own flyouts.
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    /// <summary>The gap to the taskbar and the screen edge, in pixels at 100%.</summary>
    private const int Gap = 12;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public TrayFlyoutWindow()
    {
        InitializeComponent();

        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) =>
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
    /// Shows it in the corner of the screen the cursor is on, against
    /// whichever edge the taskbar is on. Positioned in pixels: the window
    /// first moves onto that screen so it takes the screen's scaling, then
    /// sits against the taskbar at its real size.
    /// </summary>
    public void ShowNear(System.Drawing.Point cursor)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

        Show();
        UpdateLayout();

        var corner = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        if (!GetWindowRect(hwnd, out var rect))
        {
            Activate();
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var scale = VisualTreeHelperDpi();
        var gap = (int)Math.Round(Gap * scale);

        int x;
        int y;
        if (work.Bottom < bounds.Bottom)
        {
            // Taskbar at the bottom (the usual place).
            x = Math.Clamp(cursor.X - (width / 2), work.Left + gap, work.Right - width - gap);
            y = work.Bottom - height - gap;
        }
        else if (work.Top > bounds.Top)
        {
            x = Math.Clamp(cursor.X - (width / 2), work.Left + gap, work.Right - width - gap);
            y = work.Top + gap;
        }
        else if (work.Left > bounds.Left)
        {
            x = work.Left + gap;
            y = Math.Clamp(cursor.Y - (height / 2), work.Top + gap, work.Bottom - height - gap);
        }
        else if (work.Right < bounds.Right)
        {
            x = work.Right - width - gap;
            y = Math.Clamp(cursor.Y - (height / 2), work.Top + gap, work.Bottom - height - gap);
        }
        else
        {
            // An auto-hidden taskbar takes no room: the bottom right corner.
            x = work.Right - width - gap;
            y = work.Bottom - height - gap;
        }

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

        // A tray click doesn't make this app the foreground one, and without
        // that it never gets the deactivation that closes it.
        SetForegroundWindow(hwnd);
        Activate();
    }

    private double VisualTreeHelperDpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

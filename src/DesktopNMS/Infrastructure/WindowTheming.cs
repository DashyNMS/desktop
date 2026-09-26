using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Paints every window's own Windows title bar in the app's theme - the
/// surface colour, the theme's text colour and border - so Device Details,
/// Settings and the dialogs match the main window's drawn title bar. Windows
/// 11 honours the colours; Windows 10 takes only the dark/light hint. The
/// main window draws its own title bar (WindowChrome) and isn't affected.
/// </summary>
public static class WindowTheming
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>Once, at start-up, after the palette is merged (see App.ApplyTheme).</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window))
        {
            Apply(window);
        }
    }

    private static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var caption = ColorOf("SurfaceColor");
            if (caption is null)
            {
                return;
            }

            var dark = Luminance(caption.Value) < 0.5 ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            SetColor(hwnd, DwmwaCaptionColor, caption.Value);
            if (ColorOf("TextPrimaryColor") is { } text)
            {
                SetColor(hwnd, DwmwaTextColor, text);
            }

            if (ColorOf("BorderColor") is { } border)
            {
                SetColor(hwnd, DwmwaBorderColor, border);
            }
        }
        catch (Exception)
        {
            // Older Windows without these attributes keeps its own title bar.
        }
    }

    private static Color? ColorOf(string key) => Application.Current?.TryFindResource(key) is Color color ? color : null;

    private static double Luminance(Color c) => ((0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B)) / 255.0;

    // COLORREF is 0x00BBGGRR.
    private static void SetColor(IntPtr hwnd, int attribute, Color color)
    {
        var colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

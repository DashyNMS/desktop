using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Paints each window's own Windows title bar in the app's theme - the surface
/// colour, the theme's text colour and border - so Settings, the widget picker
/// and the other dialogs match the main window's drawn title bar (#250, the WPF
/// app's WindowTheming). Windows 11 honours the colours; Windows 10 takes only
/// the dark/light hint. Elsewhere the OS's title bar is left alone.
/// </summary>
public static class WindowTheming
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>Once, at start-up, after the theme is set (see ThemeSetup).</summary>
    public static void Register(Application app)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Apply(window), RoutingStrategies.Direct);

        // Dark, Light or Match the system, changed in Settings or by the OS.
        app.ActualThemeVariantChanged += (_, _) =>
        {
            if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                {
                    Apply(window);
                }
            }
        };
    }

    private static void Apply(Window window)
    {
        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero
                || ColorOf("SurfaceColor") is not { } caption)
            {
                return;
            }

            var dark = Luminance(caption) < 0.5 ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            SetColor(hwnd, DwmwaCaptionColor, caption);
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

    private static Color? ColorOf(string key)
        => Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is Color color
            ? color
            : null;

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

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Settings' Theme and Accent colour, applied to the Avalonia app (#250) - the
/// WPF app's ApplyTheme and AccentTheme. Unlike WPF, Avalonia repaints a
/// change of theme straight away, so neither needs a restart.
/// </summary>
public static class ThemeSetup
{
    public static void Apply(Application app, AppSettings settings)
    {
        app.RequestedThemeVariant = settings.Theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.System => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
        ApplyAccent(app, settings.AccentColor);
    }

    /// <summary>The accent, its low-strength tint, and black or white text that reads on it.</summary>
    public static void ApplyAccent(Application app, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out var color))
        {
            return;
        }

        app.Resources["AccentBrush"] = new SolidColorBrush(color);
        app.Resources["AccentSubtleBrush"] = new SolidColorBrush(color, 0.16);
        app.Resources["AccentForegroundBrush"] = new SolidColorBrush(ContrastingForeground(color));

        // Fluent's own controls draw focus, ticks and selection in SystemAccentColor
        // and its lighter and darker steps; without these they'd use the OS's accent.
        app.Resources["SystemAccentColor"] = color;
        for (var step = 1; step <= 3; step++)
        {
            app.Resources[$"SystemAccentColorLight{step}"] = Mix(color, Colors.White, 0.15 * step);
            app.Resources[$"SystemAccentColorDark{step}"] = Mix(color, Colors.Black, 0.15 * step);
        }
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        from.A,
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    /// <summary>Perceived (ITU-R BT.601) luminance, as the WPF app's AccentTheme decides it.</summary>
    private static Color ContrastingForeground(Color background)
    {
        var luminance = ((0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B)) / 255.0;
        return luminance > 0.6 ? Colors.Black : Colors.White;
    }

    /// <summary>The theme in effect: Dark or Light, with Match the system resolved.</summary>
    public static AppTheme Effective(Application? app)
        => app?.ActualThemeVariant == ThemeVariant.Light ? AppTheme.Light : AppTheme.Dark;
}

using System;
using System.Windows;
using System.Windows.Media;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Applies the user's chosen accent colour to the app's shared "AccentBrush"
/// resource (declared in Themes/Dark.xaml).
/// </summary>
/// <remarks>
/// Two things had to be true for this to actually repaint anything:
/// (1) WPF freezes a resource declared entirely from static XAML (no
/// bindings) the first time it is loaded, so the original brush cannot be
/// mutated in place - confirmed by an InvalidOperationException the first
/// time this tried to; replacing the dictionary entry with a brand new
/// brush avoids that. (2) Every Setter that uses this brush, in Dark.xaml
/// and every view, must reference it via DynamicResource rather than
/// StaticResource - a StaticResource reference resolves once, when the XAML
/// that declares it is first loaded (for Dark.xaml, that is during the
/// Application's own InitializeComponent, before any of this code runs),
/// and never again, so replacing the dictionary entry afterwards would have
/// no visible effect on it at all. DynamicResource re-resolves live, so
/// this can be called at any point - including while the app is already
/// running - and every consumer repaints immediately.
/// </remarks>
public static class AccentTheme
{
    public static void Apply(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !TryParseColor(hex, out var color))
        {
            return;
        }

        Application.Current.Resources["AccentBrush"] = new SolidColorBrush(color);

        // A handful of styles (the primary button, a checked nav tab, ...)
        // paint white text directly on top of this colour, which read fine
        // against the original fixed blue but goes illegible against a dark
        // accent someone actually picks (navy, near-black, dark green). This
        // picks black or white to match, so it stays readable regardless.
        Application.Current.Resources["AccentForegroundBrush"] = new SolidColorBrush(ContrastingForeground(color));
    }

    /// <summary>
    /// Perceptive (ITU-R BT.601) luminance rather than a straight RGB
    /// average - it weights green highest and blue lowest to match how the
    /// eye actually perceives brightness, so e.g. a saturated blue is judged
    /// darker than a saturated yellow of the same numeric magnitude, which a
    /// naive average would get wrong.
    /// </summary>
    private static Color ContrastingForeground(Color background)
    {
        var luminance = ((0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B)) / 255.0;
        return luminance > 0.6 ? Colors.Black : Colors.White;
    }

    public static bool TryParseColor(string? hex, out Color color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException)
        {
            // Not a recognisable colour string - caller keeps whatever it had.
        }

        return false;
    }
}

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

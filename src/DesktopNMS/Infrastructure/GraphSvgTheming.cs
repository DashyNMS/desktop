using System.Windows;
using System.Windows.Media;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Recolours a LibreNMS-rendered graph SVG for the app's current theme
/// (issue #14). RRDtool's Cairo SVG output has no background rectangle at
/// all - confirmed against a live sample - so it already sits correctly on
/// this app's own surface unchanged. The only colour that needs changing is
/// axis/legend text, which always comes back flat black regardless of
/// theme; gridlines (mid-grey) and the 2-3 data-series colours read fine on
/// either theme unchanged, so they are deliberately left alone.
/// </summary>
public static class GraphSvgTheming
{
    private const string BlackFill = "fill=\"rgb(0%, 0%, 0%)\"";

    /// <summary>
    /// Reads the *current* theme's text colour rather than assuming dark -
    /// this app also has a Light palette (Palette.Light.xaml's own
    /// TextPrimaryColor is near-black already, so recolouring there is
    /// close to a no-op, which is correct).
    /// </summary>
    public static string ApplyCurrentTheme(string svg)
    {
        if (Application.Current.Resources["TextPrimaryColor"] is not Color textColor)
        {
            return svg;
        }

        var fill = $"fill=\"rgb({Percent(textColor.R)}%, {Percent(textColor.G)}%, {Percent(textColor.B)}%)\"";
        return svg.Replace(BlackFill, fill);
    }

    private static double Percent(byte channel) => channel * 100d / 255d;
}

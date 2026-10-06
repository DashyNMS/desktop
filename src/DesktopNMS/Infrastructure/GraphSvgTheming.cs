using System.Windows;
using System.Windows.Media;
using DesktopNMS.Core.Graphs;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Restyles a LibreNMS-rendered graph SVG in the app's current theme and
/// accent (issue #14, mockups) - see <see cref="GraphSvgStyle"/>, which does
/// the work and is shared with DashyNMS Mobile. This only reads the colours.
/// </summary>
public static class GraphSvgTheming
{
    public static string ApplyCurrentTheme(string svg) => GraphSvgStyle.Apply(svg, CurrentPalette());

    /// <summary>The current theme's colours; anything missing falls back to the dark palette's.</summary>
    private static GraphPalette CurrentPalette()
    {
        var resources = Application.Current?.Resources;
        var fallback = GraphPalette.Dark;
        if (resources is null)
        {
            return fallback;
        }

        string Hex(string key, string otherwise) => resources[key] switch
        {
            Color c => $"#{c.R:X2}{c.G:X2}{c.B:X2}",
            SolidColorBrush b => $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}",
            _ => otherwise,
        };

        return fallback with
        {
            Text = Hex("TextSecondaryColor", fallback.Text),
            Grid = Hex("BorderColor", fallback.Grid),

            // The brush, not the colour: a custom accent replaces the brush (AccentTheme).
            Accent = Hex("AccentBrush", fallback.Accent),
            Ok = Hex("OkColor", fallback.Ok),
            Warning = Hex("WarningColor", fallback.Warning),
            Critical = Hex("CriticalColor", fallback.Critical),
        };
    }
}

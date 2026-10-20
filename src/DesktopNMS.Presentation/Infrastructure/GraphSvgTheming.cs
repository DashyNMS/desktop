using DesktopNMS.Core.Graphs;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Restyles a LibreNMS-rendered graph SVG in the app's current theme and
/// accent (issue #14, mockups) - see <see cref="GraphSvgStyle"/>, which does
/// the work and is shared with DashyNMS Mobile. The colours come from the
/// front end's theme (<see cref="UiPlatform.GraphTheme"/>, #249).
/// </summary>
public static class GraphSvgTheming
{
    public static string ApplyCurrentTheme(string svg) => GraphSvgStyle.Apply(svg, UiPlatform.GraphTheme.Palette);

    /// <summary>Restyled with some series left out - for the Graphs section's own legend.</summary>
    /// <param name="mainColour">The colour for the first series instead of the accent - a single sensor's own graph, kept in the colour its legend entry shows.</param>
    /// <param name="cropLegend">Cut LibreNMS's legend off - the app draws its own.</param>
    public static StyledGraph Restyle(string svg, IReadOnlySet<int>? hidden = null, string? mainColour = null, bool cropLegend = false)
    {
        var palette = UiPlatform.GraphTheme.Palette;
        return GraphSvgStyle.Restyle(svg, mainColour is null ? palette : palette with { Accent = mainColour }, hidden, cropLegend);
    }
}
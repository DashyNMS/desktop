namespace DesktopNMS.Core.Graphs;

/// <summary>
/// The names of the series in LibreNMS's device graphs, read from its own
/// graph definitions by tools/Generate-GraphSeriesNames.cs (the table is in
/// GraphSeriesNames.Generated.cs) - for the graphs the API can't name the
/// series of itself (TCP and SNMP statistics, ping response, and so on) -
/// and in its port graphs (GraphSeriesNames.Ports.cs).
/// </summary>
public static partial class GraphSeriesNames
{
    /// <summary>One series: its name, and its colour in LibreNMS's default palettes when known ("#CC0000").</summary>
    public sealed record Series(string Name, string? Colour);

    /// <param name="LegendInOrder">Whether the legend lists every series, in this order - false when some come and go (with the time range, say), so only colours can say which is which.</param>
    private sealed record Definition(IReadOnlyList<Series> Series, bool LegendInOrder);

    /// <summary>Whether there's a definition for this graph at all.</summary>
    public static bool Knows(string graphName) => Generated.ContainsKey(graphName) || Ports.ContainsKey(graphName);

    /// <summary>
    /// The names for a graph's legend entries, given each entry's colour as
    /// LibreNMS drew it ("#CC0000", in legend order) - by colour where each
    /// one names a single series, else by position where the legend always
    /// lists them all. Null when neither says for certain.
    /// </summary>
    public static IReadOnlyList<string>? Resolve(string graphName, IReadOnlyList<string> legendColours)
    {
        if (!(Generated.TryGetValue(graphName, out var definition) || Ports.TryGetValue(graphName, out definition)) || legendColours.Count == 0)
        {
            return null;
        }

        var byColour = definition.Series
            .Where(s => s.Colour is not null)
            .GroupBy(s => s.Colour!, StringComparer.OrdinalIgnoreCase)

            // A palette that wraps reuses a colour; the earlier series - the graph's main one - keeps it.
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        if (legendColours.Distinct(StringComparer.OrdinalIgnoreCase).Count() == legendColours.Count
            && legendColours.All(byColour.ContainsKey))
        {
            return legendColours.Select(c => byColour[c]).ToList();
        }

        return definition.LegendInOrder && legendColours.Count == definition.Series.Count
            ? definition.Series.Select(s => s.Name).ToList()
            : null;
    }
}

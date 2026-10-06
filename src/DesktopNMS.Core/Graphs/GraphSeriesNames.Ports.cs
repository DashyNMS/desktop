namespace DesktopNMS.Core.Graphs;

public static partial class GraphSeriesNames
{
    /// <summary>
    /// The port graphs every port has, read by hand from LibreNMS's
    /// includes/html/graphs/port/*.inc.php (at <see cref="LibreNmsCommit"/>)
    /// and the templates they use - not generated, there being only four.
    /// </summary>
    private static readonly Dictionary<string, Definition> Ports = new(StringComparer.Ordinal)
    {
        // bits.inc.php → generic_data.inc.php: the In and Out lines. A
        // "Highest" rule (rrdgraph_real_percentile) or a prediction (a range
        // into the future) adds an entry this can't name - LibreNMS's legend stays.
        ["port_bits"] = new([new("In", "#608720"), new("Out", "#606090")], LegendInOrder: true),

        // upkts.inc.php → generic_duplex.inc.php: its own line colours.
        ["port_upkts"] = new([new("In", "#330033"), new("Out", "#FF6600")], LegendInOrder: true),

        // nupkts.inc.php → generic_multi_seperated.inc.php: each one's In area, then its Out.
        ["port_nupkts"] = new([new("Broadcast in", "#085F63"), new("Broadcast out", "#49BEB7"), new("Multicast in", "#FACF5A"), new("Multicast out", "#FF5959")], LegendInOrder: true),

        // errors.inc.php → generic_multi_seperated.inc.php.
        ["port_errors"] = new([new("Errors in", "#FF3300"), new("Errors out", "#FF6633"), new("Discards in", "#805080"), new("Discards out", "#C0A060")], LegendInOrder: true),
    };
}

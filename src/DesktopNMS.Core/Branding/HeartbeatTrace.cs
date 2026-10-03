namespace DesktopNMS.Core.Branding;

/// <summary>
/// The logo's heartbeat line, and how much of it shows while something
/// loads (#228, and DashyNMS Mobile's refresh indicator): it draws in from
/// the left, then wipes away from the left, as a monitor's sweep does.
/// </summary>
/// <remarks>
/// In the logo's own 100 x 100 units, as dashynms_logo.svg draws it. Kept
/// apart from the drawing so the sweep can be tested; the app only asks
/// which part of the line to draw at each moment.
/// </remarks>
public static class HeartbeatTrace
{
    /// <summary>The line's corners: flat, up to the spike, down below, back to flat.</summary>
    public static IReadOnlyList<(double X, double Y)> Points { get; } =
        [(24, 55), (38, 55), (45.5, 33), (56.5, 71), (64, 50), (77, 50)];

    /// <summary>One sweep, in milliseconds - the mock-up's 1.15 s.</summary>
    public const uint SweepMilliseconds = 1150;

    /// <summary>The part of a sweep spent drawing in; the rest wipes it away.</summary>
    private const double DrawShare = 0.55;

    /// <summary>The whole line's length, in logo units.</summary>
    public static double Length { get; } = Enumerable.Range(1, Points.Count - 1).Sum(i => Distance(Points[i - 1], Points[i]));

    /// <summary>
    /// How far along the line the visible part starts and ends, at
    /// <paramref name="phase"/> (0 to 1) through a sweep.
    /// </summary>
    public static (double From, double To) Visible(double phase)
    {
        phase -= Math.Floor(phase);
        return phase < DrawShare
            ? (0, Length * (phase / DrawShare))
            : (Length * ((phase - DrawShare) / (1 - DrawShare)), Length);
    }

    /// <summary>The corners of the line between <paramref name="from"/> and <paramref name="to"/> along it, ends included.</summary>
    public static IReadOnlyList<(double X, double Y)> Between(double from, double to)
    {
        var result = new List<(double X, double Y)>();
        if (to <= from)
        {
            return result;
        }

        var travelled = 0.0;
        for (var i = 1; i < Points.Count; i++)
        {
            var (a, b) = (Points[i - 1], Points[i]);
            var segment = Distance(a, b);
            var start = travelled;
            var end = travelled + segment;
            travelled = end;

            if (end < from || start > to)
            {
                continue;
            }

            if (result.Count == 0)
            {
                result.Add(At(a, b, (Math.Max(from, start) - start) / segment));
            }

            result.Add(end <= to ? b : At(a, b, (to - start) / segment));
        }

        return result;
    }

    private static (double X, double Y) At((double X, double Y) a, (double X, double Y) b, double t) =>
        (a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
}

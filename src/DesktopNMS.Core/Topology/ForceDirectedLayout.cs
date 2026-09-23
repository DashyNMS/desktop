namespace DesktopNMS.Core.Topology;

/// <summary>A node's position on the network map, in map (not screen) units.</summary>
public readonly record struct MapPoint(double X, double Y);

/// <summary>
/// Lays out the network map (issue #56): a Fruchterman-Reingold force
/// layout, where every pair of nodes pushes apart and every edge pulls its
/// two ends together, cooling over a fixed number of steps so it settles.
/// A weak pull toward the centre keeps separate islands of the network from
/// drifting off. Nodes with a known position (dragged by the user, or from a
/// previous layout) stay exactly where they are and only push/pull the rest,
/// so adding one device doesn't reshuffle a map someone has already tidied.
/// Deterministic for the same input - seeded, not truly random.
/// </summary>
public static class ForceDirectedLayout
{
    /// <summary>Roughly how long an edge ends up - the layout's unit of distance.</summary>
    public const double IdealEdgeLength = 90;

    private const int Iterations = 300;
    private const double Gravity = 0.02;
    private const double RepulsionCutoffSq = IdealEdgeLength * 4 * (IdealEdgeLength * 4);

    public static Dictionary<int, MapPoint> Compute(
        IReadOnlyList<int> nodes,
        IReadOnlyList<(int A, int B)> edges,
        IReadOnlyDictionary<int, MapPoint> pinned,
        int seed = 1)
    {
        var count = nodes.Count;
        var result = new Dictionary<int, MapPoint>(count);
        if (count == 0)
        {
            return result;
        }

        var index = new Dictionary<int, int>(count);
        for (var i = 0; i < count; i++)
        {
            index[nodes[i]] = i;
        }

        var adjacency = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            adjacency[i] = new List<int>();
        }

        var edgeIndexes = new List<(int, int)>(edges.Count);
        foreach (var (a, b) in edges)
        {
            if (index.TryGetValue(a, out var ia) && index.TryGetValue(b, out var ib) && ia != ib)
            {
                edgeIndexes.Add((ia, ib));
                adjacency[ia].Add(ib);
                adjacency[ib].Add(ia);
            }
        }

        var x = new double[count];
        var y = new double[count];
        var isPinned = new bool[count];
        var random = new Random(seed);
        var k = IdealEdgeLength;
        var spread = k * Math.Sqrt(count);

        for (var i = 0; i < count; i++)
        {
            if (pinned.TryGetValue(nodes[i], out var p))
            {
                x[i] = p.X;
                y[i] = p.Y;
                isPinned[i] = true;
            }
        }

        if (isPinned.All(p => p))
        {
            return nodes.ToDictionary(n => n, n => pinned[n]);
        }

        // Unpinned nodes start next to a pinned neighbour where there is one
        // (so a new device appears beside what it's cabled to), otherwise
        // scattered around the centre.
        var (cx, cy) = Centre(x, y, isPinned);
        for (var i = 0; i < count; i++)
        {
            if (isPinned[i])
            {
                continue;
            }

            var anchors = adjacency[i].Where(j => isPinned[j]).ToList();
            if (anchors.Count > 0)
            {
                x[i] = anchors.Average(j => x[j]) + (random.NextDouble() - 0.5) * k;
                y[i] = anchors.Average(j => y[j]) + (random.NextDouble() - 0.5) * k;
            }
            else
            {
                var angle = random.NextDouble() * Math.PI * 2;
                var radius = Math.Sqrt(random.NextDouble()) * spread / 2;
                x[i] = cx + Math.Cos(angle) * radius;
                y[i] = cy + Math.Sin(angle) * radius;
            }
        }

        var dx = new double[count];
        var dy = new double[count];
        var temperature = spread / 10;
        var cooling = temperature / (Iterations + 1);

        for (var step = 0; step < Iterations; step++)
        {
            Array.Clear(dx);
            Array.Clear(dy);

            // Repulsion between every pair: k² / d.
            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    var ddx = x[i] - x[j];
                    var ddy = y[i] - y[j];
                    var distSq = ddx * ddx + ddy * ddy;

                    // Only nearby nodes push each other apart. Without this
                    // cut-off, on a few hundred nodes the combined push of
                    // everything far away stretches every edge to several
                    // times its ideal length (seen on a real 340-device
                    // fleet), leaving the fitted map too small to read.
                    if (distSq > RepulsionCutoffSq)
                    {
                        continue;
                    }

                    if (distSq < 0.01)
                    {
                        // Two nodes on the same spot - nudge apart in a
                        // seeded direction rather than dividing by zero.
                        ddx = random.NextDouble() - 0.5;
                        ddy = random.NextDouble() - 0.5;
                        distSq = 0.01;
                    }

                    var force = k * k / distSq;
                    dx[i] += ddx * force;
                    dy[i] += ddy * force;
                    dx[j] -= ddx * force;
                    dy[j] -= ddy * force;
                }
            }

            // Attraction along each edge: d² / k.
            foreach (var (a, b) in edgeIndexes)
            {
                var ddx = x[a] - x[b];
                var ddy = y[a] - y[b];
                var dist = Math.Sqrt(ddx * ddx + ddy * ddy);
                if (dist < 0.01)
                {
                    continue;
                }

                var force = dist / k;
                dx[a] -= ddx * force;
                dy[a] -= ddy * force;
                dx[b] += ddx * force;
                dy[b] += ddy * force;
            }

            for (var i = 0; i < count; i++)
            {
                if (isPinned[i])
                {
                    continue;
                }

                dx[i] += (cx - x[i]) * Gravity * k / 10;
                dy[i] += (cy - y[i]) * Gravity * k / 10;

                // Move along the net force, but never further than the
                // current temperature - that cap is what lets it settle.
                var length = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
                if (length > 0)
                {
                    var capped = Math.Min(length, temperature);
                    x[i] += dx[i] / length * capped;
                    y[i] += dy[i] / length * capped;
                }
            }

            temperature -= cooling;
        }

        for (var i = 0; i < count; i++)
        {
            result[nodes[i]] = new MapPoint(x[i], y[i]);
        }

        return result;
    }

    /// <summary>
    /// A tidy grid for nodes with no connections at all, placed below
    /// everything already laid out - so turning "show devices without links"
    /// on doesn't scatter them through the connected graph.
    /// </summary>
    public static Dictionary<int, MapPoint> Grid(IReadOnlyList<int> nodes, IReadOnlyCollection<MapPoint> existing)
    {
        var result = new Dictionary<int, MapPoint>(nodes.Count);
        if (nodes.Count == 0)
        {
            return result;
        }

        var left = existing.Count > 0 ? existing.Min(p => p.X) : 0;
        var right = existing.Count > 0 ? existing.Max(p => p.X) : 0;
        var top = existing.Count > 0 ? existing.Max(p => p.Y) + IdealEdgeLength * 1.5 : 0;

        var spacing = IdealEdgeLength * 0.9;
        var columns = Math.Max(1, (int)Math.Max(Math.Sqrt(nodes.Count), (right - left) / spacing));

        for (var i = 0; i < nodes.Count; i++)
        {
            result[nodes[i]] = new MapPoint(left + i % columns * spacing, top + i / columns * spacing);
        }

        return result;
    }

    private static (double X, double Y) Centre(double[] x, double[] y, bool[] isPinned)
    {
        double sx = 0, sy = 0;
        var n = 0;
        for (var i = 0; i < x.Length; i++)
        {
            if (isPinned[i])
            {
                sx += x[i];
                sy += y[i];
                n++;
            }
        }

        return n > 0 ? (sx / n, sy / n) : (0, 0);
    }
}

using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;

namespace DesktopNMS.Views.Maps;

/// <summary>
/// "Jiggle physics" for the maps (#207, #250): a damped spring per node that
/// is still settling, giving a visual offset from where the node really is.
/// When a dragged node's real position jumps to the cursor, its offset takes
/// up the jump, so what's drawn trails behind, overshoots and settles; nodes
/// linked to it get a smaller sympathetic kick. Purely visual - the real
/// positions (and so the saved layout) are never touched.
/// </summary>
/// <remarks>
/// Steps on a frame timer only while something is still moving, then stops,
/// so an idle map costs nothing. Only the dragged node and its neighbours
/// ever get a spring, never the whole graph, so a large map stays smooth.
/// </remarks>
internal sealed class JiggleSimulation<TNode>
    where TNode : class
{
    /// <summary>Spring stiffness and damping (per second): under-damped for a jelly-like overshoot that settles in about half a second.</summary>
    private const double Stiffness = 170;
    private const double Damping = 11;

    /// <summary>How much of a dragged node's movement linked nodes feel, as velocity.</summary>
    private const double NeighbourKick = 7;

    /// <summary>The furthest (map units) a node is ever drawn from where it really is - a very fast fling shouldn't leave it on the far side of the map.</summary>
    private const double MaxOffset = 90;

    private const double MaxStep = 1.0 / 30;

    private readonly Dictionary<TNode, Body> _bodies = new(ReferenceEqualityComparer.Instance);
    private readonly Action _redraw;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private bool _isEnabled;

    public JiggleSimulation(Action redraw)
    {
        _redraw = redraw;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => OnTick());
    }

    /// <summary>The setting. Turning it off stops anything still wobbling.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (!value)
            {
                Clear();
            }
        }
    }

    /// <summary>
    /// <paramref name="node"/>'s real position just moved by <paramref name="delta"/>:
    /// it keeps being drawn where it was and springs after it, and each of
    /// <paramref name="linked"/> gets a smaller nudge the same way.
    /// </summary>
    public void Moved(TNode node, Vector delta, IEnumerable<TNode> linked)
    {
        if (!IsEnabled || delta.SquaredLength == 0)
        {
            return;
        }

        var body = BodyFor(node);
        body.Offset = Clamp(body.Offset - delta);

        foreach (var other in linked)
        {
            if (!ReferenceEquals(other, node))
            {
                BodyFor(other).Velocity += delta * NeighbourKick;
            }
        }

        Start();
    }

    /// <summary>How far from its real position to draw <paramref name="node"/> right now (map units).</summary>
    public Vector Offset(TNode node) => _bodies.TryGetValue(node, out var body) ? body.Offset : default;

    /// <summary>
    /// Squash and stretch for a node drawn at <paramref name="centre"/>: longer
    /// along the way it's moving, thinner across it, by up to a third. Null
    /// when it's (near enough) still. <paramref name="screenScale"/> turns map
    /// units into screen pixels, so the effect looks the same at any zoom.
    /// </summary>
    public Matrix? Stretch(TNode node, Point centre, double screenScale)
    {
        if (!_bodies.TryGetValue(node, out var body))
        {
            return null;
        }

        var speed = body.Velocity.Length * screenScale;
        var amount = Math.Min(speed / 1400, 0.33);
        if (amount < 0.01)
        {
            return null;
        }

        var angle = Math.Atan2(body.Velocity.Y, body.Velocity.X);
        return Matrix.CreateTranslation(-centre.X, -centre.Y)
               * Matrix.CreateRotation(-angle)
               * Matrix.CreateScale(1 + amount, 1 / (1 + amount))
               * Matrix.CreateRotation(angle)
               * Matrix.CreateTranslation(centre.X, centre.Y);
    }

    /// <summary>Stops everything at once - the setting going off, or the map being replaced or unloaded.</summary>
    public void Clear()
    {
        _bodies.Clear();
        _timer.Stop();
    }

    private Body BodyFor(TNode node)
    {
        if (!_bodies.TryGetValue(node, out var body))
        {
            body = new Body();
            _bodies[node] = body;
        }

        return body;
    }

    private static Vector Clamp(Vector offset)
        => offset.Length > MaxOffset ? offset * (MaxOffset / offset.Length) : offset;

    private void Start()
    {
        if (!_timer.IsEnabled)
        {
            _clock.Restart();
            _timer.Start();
        }
    }

    private void OnTick()
    {
        var elapsed = Math.Min(_clock.Elapsed.TotalSeconds, MaxStep);
        _clock.Restart();

        Step(elapsed);
        _redraw();

        if (_bodies.Count == 0)
        {
            _timer.Stop();
        }
    }

    private void Step(double seconds)
    {
        // Small fixed sub-steps keep a stiff spring stable at any frame rate.
        const double subStep = 1.0 / 240;
        var settled = new List<TNode>();

        foreach (var (node, body) in _bodies)
        {
            for (var t = 0.0; t < seconds; t += subStep)
            {
                var dt = Math.Min(subStep, seconds - t);
                var acceleration = (body.Offset * -Stiffness) - (body.Velocity * Damping);
                body.Velocity += acceleration * dt;
                body.Offset += body.Velocity * dt;
            }

            body.Offset = Clamp(body.Offset);
            if (body.Offset.Length < 0.05 && body.Velocity.Length < 0.5)
            {
                settled.Add(node);
            }
        }

        foreach (var node in settled)
        {
            _bodies.Remove(node);
        }
    }

    private sealed class Body
    {
        public Vector Offset;
        public Vector Velocity;
    }
}

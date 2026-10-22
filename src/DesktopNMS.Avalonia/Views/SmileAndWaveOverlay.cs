using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace DesktopNMS.Views;

/// <summary>
/// Four penguins waddle in along the bottom of the host, stop, wave, and
/// waddle off again (#250, from the WPF app's SmileAndWaveOverlay). Drawn
/// here from plain shapes (our own penguins, not any film's character
/// designs), frame by frame from the time since <see cref="Play"/>. Purely
/// cosmetic: never hit-testable, so it never blocks the window underneath,
/// and any failure just ends the show.
/// </summary>
public sealed class SmileAndWaveOverlay : Control
{
    private const double PenguinWidth = 52;
    private const double PenguinHeight = 70;
    private const double Spacing = 64;
    private const double OverlayHeight = 130;
    private const string CaptionText = "Smile and wave, boys. Smile and wave.";

    private static readonly double[] Scales = [1.0, 0.92, 0.85, 0.78];

    private const double WalkInEnd = 1.3;
    private const double WaveEnd = 2.7;
    private const double WalkOffDuration = 1.2;
    private const double Stagger = 0.08;
    private const double ShowLength = WaveEnd + WalkOffDuration + (Stagger * 4);

    private static readonly IBrush Coat = new ImmutableSolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29));
    private static readonly IPen Outline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x4A, 0x57, 0x6E)), 1.2);
    private static readonly IPen BodyOutline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x4A, 0x57, 0x6E)), 1.5);
    private static readonly IBrush Belly = new ImmutableSolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7));
    private static readonly IBrush Orange = new ImmutableSolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;

    public SmileAndWaveOverlay()
    {
        IsHitTestVisible = false;
        Height = OverlayHeight;
        VerticalAlignment = VerticalAlignment.Bottom;
        ClipToBounds = true;
        ZIndex = 1000;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            if (_clock.Elapsed.TotalSeconds >= ShowLength)
            {
                Stop();
            }

            InvalidateVisual();
        });
    }

    public bool IsPlaying => _timer.IsEnabled;

    /// <summary>Starts the show. A no-op while one is already playing.</summary>
    public void Play()
    {
        if (!IsPlaying)
        {
            _clock.Restart();
            _timer.Start();
        }
    }

    /// <summary>Ends the show early (a click or Esc).</summary>
    public void Dismiss() => Stop();

    private void Stop()
    {
        _timer.Stop();
        _clock.Reset();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!IsPlaying)
        {
            return;
        }

        try
        {
            var t = _clock.Elapsed.TotalSeconds;
            var width = Bounds.Width;
            var groupLeft = Math.Max(8, (width - ((Spacing * (Scales.Length - 1)) + PenguinWidth)) / 2);
            var standTop = OverlayHeight - PenguinHeight - 6;

            for (var i = 0; i < Scales.Length; i++)
            {
                var standX = groupLeft + (i * Spacing);
                var delay = Stagger * i;

                // In from off the left edge (keeping the line in order), stand still
                // to wave, then off past the right edge. The front of the line sets
                // off first, so nobody catches up.
                var startX = -PenguinWidth - 20 - ((Scales.Length - 1 - i) * Spacing);
                var endX = width + 20 + (i * Spacing);
                var walkOffStart = WaveEnd + (Stagger * (Scales.Length - 1 - i));

                var x = Linear(t, (0, startX), (delay, startX), (WalkInEnd, standX), (walkOffStart, standX), (walkOffStart + WalkOffDuration, endX));
                var waddle = Waddle(t, delay, WalkInEnd) + Waddle(t, walkOffStart, walkOffStart + WalkOffDuration);
                var flipper = Flipper(t - (WalkInEnd + delay));

                DrawPenguin(context, x, standTop, Scales[i], waddle, flipper);
            }

            DrawCaption(context, width, Linear(t, (0, 0), (WalkInEnd, 0), (WalkInEnd + 0.3, 1), (WaveEnd + 0.2, 1), (WaveEnd + 0.5, 0)));
        }
        catch (Exception)
        {
            // Cosmetic only - never let it affect the window.
            _timer.Stop();
        }
    }

    /// <summary>One penguin, its feet on the bottom of a <see cref="PenguinWidth"/> x <see cref="PenguinHeight"/> box at (x, top), scaled about its feet.</summary>
    private static void DrawPenguin(DrawingContext context, double x, double top, double scale, double waddle, double flipper)
    {
        var feet = new Point(PenguinWidth / 2, PenguinHeight);
        var place = Matrix.CreateTranslation(-feet.X, -feet.Y)
                    * Matrix.CreateRotation(Radians(waddle))
                    * Matrix.CreateScale(scale, scale)
                    * Matrix.CreateTranslation(feet.X + x, feet.Y + top);
        using var penguin = context.PushTransform(place);

        // The left flipper, tucked out a little; the right one waves.
        using (context.PushTransform(Matrix.CreateTranslation(-4, -26 - 2) * Matrix.CreateRotation(Radians(15)) * Matrix.CreateTranslation(4, 26 + 2)))
        {
            context.DrawEllipse(Coat, Outline, new Rect(-1, 26, 10, 26));
        }

        using (context.PushTransform(Matrix.CreateTranslation(-48, -28) * Matrix.CreateRotation(Radians(flipper)) * Matrix.CreateTranslation(48, 28)))
        {
            context.DrawEllipse(Coat, Outline, new Rect(43, 26, 10, 26));
        }

        context.DrawEllipse(Orange, null, new Rect(9, 64, 14, 6));
        context.DrawEllipse(Orange, null, new Rect(29, 64, 14, 6));
        context.DrawEllipse(Coat, BodyOutline, new Rect(4, 4, 44, 62));
        context.DrawEllipse(Belly, null, new Rect(11, 22, 30, 42));
        context.DrawEllipse(Belly, null, new Rect(15, 13, 9, 9));
        context.DrawEllipse(Belly, null, new Rect(28, 13, 9, 9));
        context.DrawEllipse(Coat, null, new Rect(18.5, 16, 4, 4));
        context.DrawEllipse(Coat, null, new Rect(30.5, 16, 4, 4));

        var beak = new StreamGeometry();
        using (var figure = beak.Open())
        {
            figure.BeginFigure(new Point(21, 24), isFilled: true);
            figure.LineTo(new Point(31, 24));
            figure.LineTo(new Point(26, 31));
            figure.EndFigure(isClosed: true);
        }

        context.DrawGeometry(Orange, null, beak);
    }

    private void DrawCaption(DrawingContext context, double width, double opacity)
    {
        if (opacity <= 0)
        {
            return;
        }

        var text = new FormattedText(CaptionText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 13, Resource("TextPrimaryBrush", Brushes.White));
        var plate = new Rect(Math.Max(8, (width - text.Width - 24) / 2), 4, text.Width + 24, text.Height + 10);

        using (context.PushOpacity(opacity))
        {
            context.DrawRectangle(Resource("SurfaceAltBrush", Brushes.DimGray), new Pen(Resource("BorderBrush", Brushes.Gray), 1), plate, 12, 12);
            context.DrawText(text, new Point(plate.X + 12, plate.Y + 5));
        }
    }

    private IBrush Resource(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    /// <summary>A side-to-side rock between <paramref name="from"/> and <paramref name="to"/>, starting and ending upright.</summary>
    private static double Waddle(double t, double from, double to)
    {
        if (t <= from || t >= to)
        {
            return 0;
        }

        const double step = 0.15;
        var swing = Math.Sin((t - from) / step * Math.PI / 2);
        var ease = Math.Min(1, Math.Min(t - from, to - t) / step);
        return 7 * swing * ease;
    }

    /// <summary>The right flipper, <paramref name="t"/> seconds into its wave: up, three waves, and down again.</summary>
    private static double Flipper(double t)
    {
        (double At, double Angle)[] frames = [(0.0, -15), (0.2, -140), (0.4, -105), (0.6, -140), (0.8, -105), (1.0, -140), (1.25, -15)];
        if (t <= 0 || t >= frames[^1].At)
        {
            return -15;
        }

        for (var i = 1; i < frames.Length; i++)
        {
            if (t <= frames[i].At)
            {
                var p = (t - frames[i - 1].At) / (frames[i].At - frames[i - 1].At);
                var eased = (1 - Math.Cos(p * Math.PI)) / 2;
                return frames[i - 1].Angle + ((frames[i].Angle - frames[i - 1].Angle) * eased);
            }
        }

        return -15;
    }

    /// <summary>A straight-line value through key frames, held at the ends.</summary>
    private static double Linear(double t, params (double At, double Value)[] frames)
    {
        if (t <= frames[0].At)
        {
            return frames[0].Value;
        }

        for (var i = 1; i < frames.Length; i++)
        {
            if (t <= frames[i].At)
            {
                var span = frames[i].At - frames[i - 1].At;
                var p = span <= 0 ? 1 : (t - frames[i - 1].At) / span;
                return frames[i - 1].Value + ((frames[i].Value - frames[i - 1].Value) * p);
            }
        }

        return frames[^1].Value;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}

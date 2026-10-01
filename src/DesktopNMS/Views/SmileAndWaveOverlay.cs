using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DesktopNMS.Views;

/// <summary>
/// Four penguins waddle in along the bottom of the host, stop, wave, and
/// waddle off again. Drawn here from plain shapes (our own penguins, not any
/// film's character designs). Purely cosmetic: never hit-testable, so it
/// never blocks the window underneath, and <see cref="Play"/> swallows any
/// failure rather than letting a bit of fun take a window down.
/// </summary>
public sealed class SmileAndWaveOverlay : Canvas
{
    private const double PenguinWidth = 52;
    private const double PenguinHeight = 70;
    private const double Spacing = 64;
    private const double OverlayHeight = 130;

    private static readonly double[] Scales = { 1.0, 0.92, 0.85, 0.78 };

    private static readonly TimeSpan WalkInEnd = TimeSpan.FromSeconds(1.3);
    private static readonly TimeSpan WaveEnd = TimeSpan.FromSeconds(2.7);
    private static readonly TimeSpan WalkOffDuration = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan Stagger = TimeSpan.FromSeconds(0.08);

    private Storyboard? _storyboard;

    public SmileAndWaveOverlay()
    {
        IsHitTestVisible = false;
        Height = OverlayHeight;
        VerticalAlignment = VerticalAlignment.Bottom;
        ClipToBounds = true;
        Panel.SetZIndex(this, 1000);
    }

    public bool IsPlaying => _storyboard is not null;

    /// <summary>Raised once the penguins have gone, whether they finished or were dismissed.</summary>
    public event EventHandler? Finished;

    /// <summary>Starts the show across <paramref name="width"/> pixels. A no-op while one is already playing.</summary>
    public void Play(double width)
    {
        if (IsPlaying)
        {
            return;
        }

        try
        {
            Children.Clear();
            var storyboard = new Storyboard();
            var reducedMotion = !SystemParameters.ClientAreaAnimation;

            var groupWidth = Spacing * (Scales.Length - 1) + PenguinWidth;
            var groupLeft = Math.Max(8, (width - groupWidth) / 2);
            var standTop = OverlayHeight - PenguinHeight - 6;

            for (var i = 0; i < Scales.Length; i++)
            {
                var penguin = new Penguin(Scales[i]);
                Canvas.SetTop(penguin.Root, standTop);
                Children.Add(penguin.Root);

                var standX = groupLeft + i * Spacing;
                var delay = TimeSpan.FromTicks(Stagger.Ticks * i);

                if (reducedMotion)
                {
                    penguin.Walk.X = standX;
                    penguin.Flipper.Angle = -140;
                }
                else
                {
                    // Walk in from off the left edge, keeping the line in order.
                    var startX = -PenguinWidth - 20 - (Scales.Length - 1 - i) * Spacing;
                    var endX = width + 20 + i * Spacing;
                    penguin.Walk.X = startX;

                    Add(storyboard, penguin.Walk, TranslateTransform.XProperty,
                        new DoubleAnimation(startX, standX, WalkInEnd - delay) { BeginTime = delay });
                    Add(storyboard, penguin.Walk, TranslateTransform.XProperty,
                        new DoubleAnimation(standX, endX, WalkOffDuration) { BeginTime = WaveEnd + delay });

                    Add(storyboard, penguin.Waddle, RotateTransform.AngleProperty, Waddle(delay, WalkInEnd));
                    Add(storyboard, penguin.Waddle, RotateTransform.AngleProperty, Waddle(WaveEnd + delay, WaveEnd + delay + WalkOffDuration));
                }

                Add(storyboard, penguin.Flipper, RotateTransform.AngleProperty, Wave(WalkInEnd + delay, reducedMotion));
            }

            var caption = BuildCaption();
            Children.Add(caption);
            caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(caption, Math.Max(8, (width - caption.DesiredSize.Width) / 2));
            Canvas.SetTop(caption, 4);
            Add(storyboard, caption, OpacityProperty, Fade(WalkInEnd, WaveEnd + TimeSpan.FromSeconds(0.2)));

            if (reducedMotion)
            {
                // Nothing walks, so fade the penguins in and out with the caption instead.
                Add(storyboard, this, OpacityProperty, Fade(TimeSpan.Zero, TimeSpan.FromSeconds(3.4)));
            }

            storyboard.Completed += (_, _) => Stop();
            _storyboard = storyboard;
            storyboard.Begin(this, true);
        }
        catch (Exception)
        {
            Stop();
        }
    }

    /// <summary>Ends the show early (a click or Esc).</summary>
    public void Dismiss() => Stop();

    private void Stop()
    {
        try
        {
            _storyboard?.Stop(this);
        }
        catch (Exception)
        {
            // Cosmetic only - nothing to recover.
        }

        var wasPlaying = _storyboard is not null;
        _storyboard = null;
        Children.Clear();
        Opacity = 1;

        if (wasPlaying)
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void Add(Storyboard storyboard, DependencyObject target, DependencyProperty property, Timeline animation)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }

    /// <summary>A side-to-side rock while walking, always settling back upright.</summary>
    private static DoubleAnimationUsingKeyFrames Waddle(TimeSpan from, TimeSpan to)
    {
        var frames = new DoubleAnimationUsingKeyFrames { BeginTime = from };
        var length = to - from;
        var step = TimeSpan.FromSeconds(0.15);
        var sign = 1;
        var at = TimeSpan.Zero;

        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        while (at + step < length)
        {
            at += step;
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(7 * sign, KeyTime.FromTimeSpan(at)));
            sign = -sign;
        }

        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(length)));
        return frames;
    }

    /// <summary>Raise the right flipper, wave it three times, and put it down again.</summary>
    private static DoubleAnimationUsingKeyFrames Wave(TimeSpan from, bool reducedMotion)
    {
        var frames = new DoubleAnimationUsingKeyFrames { BeginTime = from };
        if (reducedMotion)
        {
            // Already raised; just hold it there.
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(-140, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            return frames;
        }

        var times = new List<(double Seconds, double Angle)>
        {
            (0, -15), (0.2, -140), (0.4, -105), (0.6, -140), (0.8, -105), (1.0, -140), (1.25, -15),
        };

        foreach (var (seconds, angle) in times)
        {
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(angle, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        }

        return frames;
    }

    private static DoubleAnimationUsingKeyFrames Fade(TimeSpan inAt, TimeSpan outAt)
    {
        var frames = new DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(inAt)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(inAt + TimeSpan.FromSeconds(0.3))));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(outAt)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(outAt + TimeSpan.FromSeconds(0.3))));
        return frames;
    }

    private Border BuildCaption() => new()
    {
        Opacity = 0,
        Padding = new Thickness(12, 5, 12, 5),
        CornerRadius = new CornerRadius(12),
        Background = TryFindResource("SurfaceAltBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x30)),
        BorderBrush = TryFindResource("BorderBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x2C, 0x34, 0x42)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = "Smile and wave, boys. Smile and wave.",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White,
        },
    };

    /// <summary>One penguin, drawn in a <see cref="PenguinWidth"/> x <see cref="PenguinHeight"/> box with its feet on the bottom edge.</summary>
    private sealed class Penguin
    {
        private static readonly Brush Coat = Frozen(Color.FromRgb(0x1A, 0x1F, 0x29));
        private static readonly Brush Outline = Frozen(Color.FromRgb(0x4A, 0x57, 0x6E));
        private static readonly Brush Belly = Frozen(Color.FromRgb(0xF2, 0xF4, 0xF7));
        private static readonly Brush Orange = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));

        public Penguin(double scale)
        {
            var body = new Canvas { Width = PenguinWidth, Height = PenguinHeight };

            body.Children.Add(Shape(new Ellipse { Width = 10, Height = 26, Fill = Coat, Stroke = Outline, StrokeThickness = 1.2 }, -1, 26,
                new RotateTransform(15, 5, 2)));
            Flipper = new RotateTransform(-15);
            var flipper = Shape(new Ellipse { Width = 10, Height = 26, Fill = Coat, Stroke = Outline, StrokeThickness = 1.2 }, 43, 26, Flipper);
            flipper.RenderTransformOrigin = new Point(0.5, 0.08);
            body.Children.Add(flipper);

            body.Children.Add(Shape(new Ellipse { Width = 14, Height = 6, Fill = Orange }, 9, 64));
            body.Children.Add(Shape(new Ellipse { Width = 14, Height = 6, Fill = Orange }, 29, 64));
            body.Children.Add(Shape(new Ellipse { Width = 44, Height = 62, Fill = Coat, Stroke = Outline, StrokeThickness = 1.5 }, 4, 4));
            body.Children.Add(Shape(new Ellipse { Width = 30, Height = 42, Fill = Belly }, 11, 22));
            body.Children.Add(Shape(new Ellipse { Width = 9, Height = 9, Fill = Belly }, 15, 13));
            body.Children.Add(Shape(new Ellipse { Width = 9, Height = 9, Fill = Belly }, 28, 13));
            body.Children.Add(Shape(new Ellipse { Width = 4, Height = 4, Fill = Coat }, 18.5, 16));
            body.Children.Add(Shape(new Ellipse { Width = 4, Height = 4, Fill = Coat }, 30.5, 16));
            body.Children.Add(new Polygon
            {
                Points = new PointCollection { new(21, 24), new(31, 24), new(26, 31) },
                Fill = Orange,
            });

            Waddle = new RotateTransform(0, PenguinWidth / 2, PenguinHeight);
            body.RenderTransform = Waddle;

            Walk = new TranslateTransform();
            Root = new Canvas { Width = PenguinWidth, Height = PenguinHeight };
            Root.Children.Add(body);
            Root.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(scale, scale, PenguinWidth / 2, PenguinHeight),
                    Walk,
                },
            };
        }

        public Canvas Root { get; }

        public TranslateTransform Walk { get; }

        public RotateTransform Waddle { get; }

        public RotateTransform Flipper { get; }

        private static UIElement Shape(UIElement shape, double left, double top, Transform? transform = null)
        {
            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);
            if (transform is not null)
            {
                shape.RenderTransform = transform;
            }

            return shape;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}

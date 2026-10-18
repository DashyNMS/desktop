using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopNMS.Views;

/// <summary>
/// A slow, soft glow in the accent colour behind the Intelligence
/// conversation: three blurred light pools drifting and breathing. Purely
/// decorative - it only runs while on screen, and holds still when Windows
/// animations are turned off.
/// </summary>
public sealed class AmbientGlow : Canvas
{
    private static readonly (double X, double Y, double Size, double DriftX, double DriftY, double Seconds, double Opacity)[] Pools =
    {
        (0.30, 0.35, 0.55, 0.10, 0.06, 18, 0.22),
        (0.68, 0.45, 0.48, -0.08, 0.08, 23, 0.16),
        (0.50, 0.70, 0.40, 0.06, -0.07, 29, 0.12),
    };

    private readonly Ellipse[] _ellipses = new Ellipse[Pools.Length];
    private bool _running;

    public AmbientGlow()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        for (var i = 0; i < Pools.Length; i++)
        {
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Opacity = Pools[i].Opacity,
                RenderTransform = new TranslateTransform(),
            };
            _ellipses[i] = new Ellipse(ellipse);
            Children.Add(ellipse);
        }

        SizeChanged += (_, _) => Layout();
        IsVisibleChanged += (_, _) => UpdateRunning();
        Loaded += (_, _) =>
        {
            Layout();
            UpdateRunning();
        };
        Unloaded += (_, _) => Stop();
    }

    private void Layout()
    {
        var accent = (TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x3B, 0x82, 0xF6);
        for (var i = 0; i < Pools.Length; i++)
        {
            var pool = Pools[i];
            var size = Math.Max(ActualWidth, ActualHeight) * pool.Size;
            var shape = _ellipses[i].Shape;
            shape.Width = size;
            shape.Height = size;
            shape.Fill = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0xFF, accent.R, accent.G, accent.B), 0),
                    new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1),
                });
            SetLeft(shape, ActualWidth * pool.X - size / 2);
            SetTop(shape, ActualHeight * pool.Y - size / 2);
        }

        if (_running)
        {
            Stop();
            UpdateRunning();
        }
    }

    private void UpdateRunning()
    {
        var shouldRun = IsVisible && SystemParameters.ClientAreaAnimation && ActualWidth > 0;
        if (shouldRun && !_running)
        {
            Start();
        }
        else if (!shouldRun && _running)
        {
            Stop();
        }
    }

    private void Start()
    {
        _running = true;
        for (var i = 0; i < Pools.Length; i++)
        {
            var pool = Pools[i];
            var shape = _ellipses[i].Shape;
            var move = (TranslateTransform)shape.RenderTransform;
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var duration = TimeSpan.FromSeconds(pool.Seconds);

            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, ActualWidth * pool.DriftX, duration) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, ActualHeight * pool.DriftY, duration * 0.8) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            shape.BeginAnimation(OpacityProperty, new DoubleAnimation(pool.Opacity * 0.55, pool.Opacity, duration * 0.6) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        }
    }

    private void Stop()
    {
        _running = false;
        foreach (var ellipse in _ellipses)
        {
            var move = (TranslateTransform)ellipse.Shape.RenderTransform;
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            ellipse.Shape.BeginAnimation(OpacityProperty, null);
        }
    }

    private sealed record Ellipse(System.Windows.Shapes.Ellipse Shape);
}

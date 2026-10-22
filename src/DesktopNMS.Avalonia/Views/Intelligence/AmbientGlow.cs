using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace DesktopNMS.Views.Intelligence;

/// <summary>
/// A slow, soft glow in the accent colour behind the Intelligence
/// conversation (#250, from the WPF app's AmbientGlow): three blurred light
/// pools drifting and breathing. Purely decorative - it only runs while on
/// screen, a few frames a second, which is plenty for something this slow.
/// </summary>
public sealed class AmbientGlow : Control
{
    private static readonly (double X, double Y, double Size, double DriftX, double DriftY, double Seconds, double Opacity)[] Pools =
    [
        (0.30, 0.35, 0.55, 0.10, 0.06, 18, 0.22),
        (0.68, 0.45, 0.48, -0.08, 0.08, 23, 0.16),
        (0.50, 0.70, 0.40, 0.06, -0.07, 29, 0.12),
    ];

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;

    public AmbientGlow()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) => InvalidateVisual());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateRunning();
        }
    }

    private void UpdateRunning()
    {
        if (IsEffectivelyVisible && VisualRoot is not null)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        // The tab can be hidden by an ancestor without this control's own visibility changing.
        if (!IsEffectivelyVisible)
        {
            _timer.Stop();
            return;
        }

        var accent = this.TryFindResource("AccentBrush", ActualThemeVariant, out var resource) && resource is ISolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0x3B, 0x82, 0xF6);
        var width = Bounds.Width;
        var height = Bounds.Height;
        var seconds = _clock.Elapsed.TotalSeconds;

        foreach (var pool in Pools)
        {
            var size = Math.Max(width, height) * pool.Size;

            // Each drifts there and back on an ease-in-out curve, breathing a little out of step with its drift.
            var dx = width * pool.DriftX * Wave(seconds, pool.Seconds);
            var dy = height * pool.DriftY * Wave(seconds, pool.Seconds * 0.8);
            var opacity = pool.Opacity * (0.55 + (0.45 * Wave(seconds, pool.Seconds * 0.6)));

            var centre = new Point((width * pool.X) + dx, (height * pool.Y) + dy);
            var fill = new RadialGradientBrush
            {
                Opacity = opacity,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xFF, accent.R, accent.G, accent.B), 0),
                    new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1),
                },
            };
            context.DrawEllipse(fill, null, centre, size / 2, size / 2);
        }
    }

    /// <summary>0 to 1 and back over twice <paramref name="period"/> seconds, eased at each end.</summary>
    private static double Wave(double seconds, double period) => (1 - Math.Cos(seconds / period * Math.PI)) / 2;
}

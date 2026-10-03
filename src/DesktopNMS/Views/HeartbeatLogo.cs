using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopNMS.Core.Branding;

namespace DesktopNMS.Views;

/// <summary>
/// The DashyNMS mark, drawn rather than an image so its heartbeat can move
/// (#228, as on DashyNMS Mobile): while <see cref="IsBeating"/> the line
/// sweeps in and away as a heart monitor's does, and at rest it's the logo.
/// The app's loading indicator in place of a spinner - the launch screen,
/// <see cref="LoadingOverlay"/> and the status bars' "Refreshing".
/// </summary>
/// <remarks>
/// Only animates while beating, loaded and visible. With Windows animations
/// turned off it fades gently in and out instead of sweeping.
/// </remarks>
public sealed class HeartbeatLogo : FrameworkElement
{
    public static readonly DependencyProperty IsBeatingProperty = DependencyProperty.Register(
        nameof(IsBeating), typeof(bool), typeof(HeartbeatLogo),
        new FrameworkPropertyMetadata(false, (d, _) => ((HeartbeatLogo)d).UpdateAnimation()));

    /// <summary>Greyed out, as the tray icon is while not connected (#232).</summary>
    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted), typeof(bool), typeof(HeartbeatLogo),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x23)));
    private static readonly Pen Ring = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))), 7.5));
    private static readonly Pen Line = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF6))), 8.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        });

    private static readonly Pen MutedRing = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x57, 0x60, 0x6A))), 7.5));
    private static readonly Pen MutedLine = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x57, 0x60, 0x6A))), 8.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        });

    private readonly Stopwatch _clock = new();
    private bool _sweeping;
    private bool _fading;
    private double? _phase;

    public HeartbeatLogo()
    {
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => UpdateAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public bool IsBeating
    {
        get => (bool)GetValue(IsBeatingProperty);
        set => SetValue(IsBeatingProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        // The logo's 100 x 100 units, scaled to fit and centred.
        var scale = Math.Min(ActualWidth, ActualHeight) / 100;
        if (scale <= 0)
        {
            return;
        }

        drawingContext.PushTransform(new TranslateTransform((ActualWidth / 2) - (50 * scale), (ActualHeight / 2) - (50 * scale)));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));

        drawingContext.DrawEllipse(Background, null, new Point(50, 50), 46, 46);
        drawingContext.DrawEllipse(null, IsMuted ? MutedRing : Ring, new Point(50, 50), 42.25, 42.25);

        var (from, to) = _phase is { } phase ? HeartbeatTrace.Visible(phase) : (0, HeartbeatTrace.Length);
        var points = HeartbeatTrace.Between(from, to);
        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false, isClosed: false);
                for (var i = 1; i < points.Count; i++)
                {
                    context.LineTo(new Point(points[i].X, points[i].Y), isStroked: true, isSmoothJoin: true);
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, IsMuted ? MutedLine : Line, geometry);
        }

        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void UpdateAnimation()
    {
        var run = IsBeating && IsLoaded && IsVisible;
        var reduced = !SystemParameters.ClientAreaAnimation;

        if (run && !reduced && !_sweeping)
        {
            StopFading();
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
            _sweeping = true;
        }
        else if (run && reduced && !_fading)
        {
            StopSweeping();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
            _fading = true;
        }
        else if (!run)
        {
            StopSweeping();
            StopFading();
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _phase = _clock.Elapsed.TotalMilliseconds / HeartbeatTrace.SweepMilliseconds;
        InvalidateVisual();
    }

    private void StopSweeping()
    {
        if (!_sweeping)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
        _sweeping = false;

        // At rest: the whole line again.
        _phase = null;
        InvalidateVisual();
    }

    private void StopFading()
    {
        if (_fading)
        {
            BeginAnimation(OpacityProperty, null);
            _fading = false;
        }
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

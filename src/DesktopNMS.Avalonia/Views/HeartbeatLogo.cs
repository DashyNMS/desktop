using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using DesktopNMS.Core.Branding;

namespace DesktopNMS.Views;

/// <summary>
/// The DashyNMS mark, drawn rather than an image so its heartbeat can move
/// (#228, #250, as on DashyNMS Mobile): while <see cref="IsBeating"/> the
/// line sweeps in and away as a heart monitor's does, and at rest it's the
/// logo. The app's loading indicator in place of a spinner - the loading
/// overlay and the status bars' "Refreshing".
/// </summary>
/// <remarks>Only animates while beating and on screen.</remarks>
public sealed class HeartbeatLogo : Control
{
    public static readonly StyledProperty<bool> IsBeatingProperty =
        AvaloniaProperty.Register<HeartbeatLogo, bool>(nameof(IsBeating));

    /// <summary>Greyed out, as the tray icon is while not connected (#232).</summary>
    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<HeartbeatLogo, bool>(nameof(IsMuted));

    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x23));
    private static readonly IPen Ring = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), 7.5);
    private static readonly IPen Line = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF6)), 8.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen MutedRing = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x57, 0x60, 0x6A)), 7.5);
    private static readonly IPen MutedLine = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x57, 0x60, 0x6A)), 8.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private double? _phase;

    static HeartbeatLogo()
    {
        AffectsRender<HeartbeatLogo>(IsMutedProperty);
    }

    public HeartbeatLogo()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            _phase = _clock.Elapsed.TotalMilliseconds / HeartbeatTrace.SweepMilliseconds;
            InvalidateVisual();
        });
    }

    public bool IsBeating { get => GetValue(IsBeatingProperty); set => SetValue(IsBeatingProperty, value); }

    public bool IsMuted { get => GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsBeatingProperty || change.Property == IsVisibleProperty)
        {
            UpdateAnimation();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        var run = IsBeating && IsEffectivelyVisible && VisualRoot is not null;
        if (run && !_timer.IsEnabled)
        {
            _clock.Restart();
            _timer.Start();
        }
        else if (!run && _timer.IsEnabled)
        {
            _timer.Stop();
            _clock.Stop();

            // At rest: the whole line again.
            _phase = null;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        // The logo's 100 x 100 units, scaled to fit and centred.
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 100;
        if (scale <= 0)
        {
            return;
        }

        // A parent can hide the logo without its own visibility changing - stop
        // then too, once this frame's drawn: not during it, which can't ask
        // for another.
        if (_timer.IsEnabled && !IsEffectivelyVisible)
        {
            Dispatcher.UIThread.Post(UpdateAnimation);
        }

        using var fit = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width / 2) - (50 * scale), (Bounds.Height / 2) - (50 * scale)));

        context.DrawEllipse(Background, null, new Point(50, 50), 46, 46);
        context.DrawEllipse(null, IsMuted ? MutedRing : Ring, new Point(50, 50), 42.25, 42.25);

        var (from, to) = _phase is { } phase ? HeartbeatTrace.Visible(phase) : (0, HeartbeatTrace.Length);
        var points = HeartbeatTrace.Between(from, to);
        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var figure = geometry.Open())
            {
                figure.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false);
                for (var i = 1; i < points.Count; i++)
                {
                    figure.LineTo(new Point(points[i].X, points[i].Y));
                }

                figure.EndFigure(isClosed: false);
            }

            context.DrawGeometry(null, IsMuted ? MutedLine : Line, geometry);
        }
    }
}

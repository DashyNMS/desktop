using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopNMS.Views;

/// <summary>A cosmetic companion using the same drawing as the hidden penguins.</summary>
public sealed class IntelligenceMascot : Viewbox
{
    private readonly SmileAndWaveOverlay.Penguin _penguin = new(1);

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(IntelligenceMascot),
        new PropertyMetadata(false, OnIsBusyChanged));

    public IntelligenceMascot()
    {
        // Leave room for the flipper, so waving never overlaps nearby controls.
        var canvas = new Canvas { Width = 76, Height = 80, IsHitTestVisible = false };
        Canvas.SetLeft(_penguin.Root, 12);
        Canvas.SetTop(_penguin.Root, 5);
        canvas.Children.Add(_penguin.Root);
        Child = canvas;
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        System.Windows.Automation.AutomationProperties.SetName(this, "Penguin mascot");
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    private static void OnIsBusyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((IntelligenceMascot)sender).UpdateAnimation();

    private void StopAnimation()
    {
        _penguin.Waddle.BeginAnimation(RotateTransform.AngleProperty, null);
        _penguin.Flipper.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void UpdateAnimation()
    {
        StopAnimation();
        if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation) return;

        if (IsBusy)
        {
            _penguin.Waddle.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
            {
                From = -5, To = 5, Duration = TimeSpan.FromSeconds(0.45),
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        else
        {
            _penguin.Flipper.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
            {
                From = -15, To = -135, Duration = TimeSpan.FromSeconds(0.4),
                AutoReverse = true, RepeatBehavior = new RepeatBehavior(2),
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
    }
}

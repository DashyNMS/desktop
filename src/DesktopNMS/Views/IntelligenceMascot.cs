using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopNMS.Views;

/// <summary>A companion perched by the composer. Its reactions stay local.</summary>
public sealed class IntelligenceMascot : Grid
{
    private readonly SmileAndWaveOverlay.Penguin _penguin = new(1);
    private readonly Canvas _happyFace = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _caption = new() { FontSize = 12 };
    private readonly DispatcherTimer _greeting = new() { Interval = TimeSpan.FromSeconds(2) };

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(IntelligenceMascot), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.Register(
        nameof(HasError), typeof(bool), typeof(IntelligenceMascot), new PropertyMetadata(false, OnStateChanged));

    public IntelligenceMascot()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var name = new TextBlock { Text = "DASHY", FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        words.Children.Add(name);
        words.Children.Add(_caption);
        Children.Add(words);

        var drawing = new Canvas { Width = 80, Height = 82, IsHitTestVisible = false };
        var shadow = new Ellipse { Width = 42, Height = 5, Fill = Brushes.Black, Opacity = 0.12 };
        Canvas.SetLeft(shadow, 19);
        Canvas.SetTop(shadow, 74);
        drawing.Children.Add(shadow);
        Canvas.SetLeft(_penguin.Root, 14);
        Canvas.SetTop(_penguin.Root, 5);
        drawing.Children.Add(_penguin.Root);
        var body = (Canvas)_penguin.Root.Children[0];
        body.Children.Add(_happyFace);
        foreach (var x in new[] { 15.0, 28.0 })
        {
            var eye = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)) };
            Canvas.SetLeft(eye, x);
            Canvas.SetTop(eye, 13);
            _happyFace.Children.Add(eye);
            // Happy crescent eyes replace the pupils while Dashy waves.
            var arc = new Path
            {
                Data = Geometry.Parse("M 1,6 Q 4.5,1 8,6"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29)),
                StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            Canvas.SetLeft(arc, x);
            Canvas.SetTop(arc, 13);
            _happyFace.Children.Add(arc);
        }
        _happyFace.Children.Add(new Path
        {
            Data = Geometry.Parse("M 22,33 Q 26,37 30,33"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29)),
            StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        var hello = new Button
        {
            Content = new Viewbox { Child = drawing, Width = 72, Height = 74 },
            Width = 76, Height = 76,
            ToolTip = "Say hello to Dashy", Cursor = Cursors.Hand,
            Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        };
        // Keep keyboard activation and focus, using the app's existing button style.
        hello.SetResourceReference(StyleProperty, "IconButtonStyle");
        System.Windows.Automation.AutomationProperties.SetName(hello, "Dashy Companion: smile and wave");
        hello.Click += (_, _) => Greet();
        SetColumn(hello, 1);
        Children.Add(hello);
        _greeting.Tick += (_, _) => { _greeting.Stop(); UpdateAnimation(); };
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public bool HasError { get => (bool)GetValue(HasErrorProperty); set => SetValue(HasErrorProperty, value); }

    private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var companion = (IntelligenceMascot)sender;
        if (args.Property == IsBusyProperty && args.OldValue is true && args.NewValue is false && !companion.HasError)
            companion.Greet("Back with you!");
        else companion.UpdateAnimation();
    }

    private void StopAnimation()
    {
        _greeting.Stop();
        _penguin.Waddle.BeginAnimation(RotateTransform.AngleProperty, null);
        _penguin.Flipper.BeginAnimation(RotateTransform.AngleProperty, null);
        _penguin.Walk.BeginAnimation(TranslateTransform.YProperty, null);
        _happyFace.Visibility = Visibility.Collapsed;
    }

    private void Greet(string text = "Hello there!")
    {
        StopAnimation();
        if (!IsLoaded || !IsVisible) return;
        _caption.Text = text;
        _happyFace.Visibility = Visibility.Visible;
        _greeting.Start();
        if (!SystemParameters.ClientAreaAnimation) return;
        _penguin.Flipper.BeginAnimation(RotateTransform.AngleProperty, Motion(-15, -135, 0.3, new RepeatBehavior(3)));
        _penguin.Walk.BeginAnimation(TranslateTransform.YProperty, Motion(0, -3, 0.3, new RepeatBehavior(3)));
    }

    private void UpdateAnimation()
    {
        StopAnimation();
        _caption.Text = HasError ? "Let's try again." : IsBusy ? "On the case…" : "Ready when you are.";
        if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation) return;
        if (IsBusy)
            _penguin.Waddle.BeginAnimation(RotateTransform.AngleProperty, Motion(-4, 4, 0.5, RepeatBehavior.Forever));
        else
            _penguin.Walk.BeginAnimation(TranslateTransform.YProperty, Motion(0, -1.5, 2.4, RepeatBehavior.Forever));
    }

    private static DoubleAnimation Motion(double from, double to, double seconds, RepeatBehavior repeat) => new()
    {
        From = from, To = to, Duration = TimeSpan.FromSeconds(seconds), AutoReverse = true,
        RepeatBehavior = repeat, FillBehavior = FillBehavior.Stop,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
    };
}

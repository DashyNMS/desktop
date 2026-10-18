using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopNMS.Views;

/// <summary>A small pixel companion. Greetings are local and never send a chat message.</summary>
public sealed class IntelligenceMascot : Button
{
    private readonly Canvas _body = new() { Width = 64, Height = 80 };
    private readonly Canvas _happyFace = new() { Visibility = Visibility.Collapsed };
    private readonly RotateTransform _flipper = new(-10, 4, 4);
    private readonly Canvas _restingFlipper = new() { Width = 12, Height = 28 };
    private readonly Canvas _waveA = new() { Opacity = 0 };
    private readonly Canvas _waveB = new() { Opacity = 0 };
    private readonly TranslateTransform _bounce = new();
    private readonly DispatcherTimer _greeting = new() { Interval = TimeSpan.FromSeconds(2) };

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(IntelligenceMascot), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.Register(
        nameof(HasError), typeof(bool), typeof(IntelligenceMascot), new PropertyMetadata(false, OnStateChanged));

    public IntelligenceMascot()
    {
        Width = 40;
        Height = 44;
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Cursor = Cursors.Hand;
        ToolTip = "Say hello to Dashy";
        // No button chrome or hover/pressed fill. WPF's focus visual is only
        // shown for keyboard navigation, so a mouse click stays unhighlighted.
        Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
                <Grid Background="Transparent">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Grid>
            </ControlTemplate>
            """);
        FocusVisualStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Control">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Control">
                            <Rectangle Margin="2" RadiusX="6" RadiusY="6" Stroke="{DynamicResource TextSecondaryBrush}"
                                       StrokeDashArray="1 3" IsHitTestVisible="False" />
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """);
        System.Windows.Automation.AutomationProperties.SetName(this, "Dashy Companion: smile and wave");

        var drawing = new Canvas { Width = 80, Height = 88, IsHitTestVisible = false };
        RenderOptions.SetEdgeMode(drawing, EdgeMode.Aliased);
        Pixel(drawing, 24, 82, 32, 2, new SolidColorBrush(Color.FromArgb(35, 100, 116, 139)));
        Canvas.SetLeft(_body, 8);
        Canvas.SetTop(_body, 2);
        _body.RenderTransform = _bounce;
        drawing.Children.Add(_body);
        // Each cell is a whole 4px square; no interpolation or scaled bitmap assets.
        string[] sprite =
        {
            ".....KKKKKK.....",
            "....KCCCCCCK....",
            "...KCHCCCCCCK...",
            "...KCCCCCCCCK...",
            "..KCCWWCCWWCCK..",
            "..KCWKWCCWKWCK..",
            "..KCWWWCCWWWCK..",
            "..KCCWWOOWWCCK..",
            "..KCCWOOOOWCCK..",
            ".KKCCWWOOWWCCK..",
            "KCKCCWWWWWWCCK..",
            "KCKCWWWWWWWWCK..",
            "KCKCWWWWWWWWCK..",
            ".KKCWWWWWWWWCK..",
            "..KCWWWWWWWWCK..",
            "..KCCWWWWWWCCK..",
            "...KCCWWWWCCK...",
            "....KKKKKKKK....",
            "...OOOOKKOOOO...",
            "..OOOOO..OOOOO..",
        };
        Paint(_body, sprite);
        var flipper = _restingFlipper;
        flipper.RenderTransform = _flipper;
        Paint(flipper, new[] { "KK.", "CCK", "CCK", "CCK", "CCK", "CK.", "K.." });
        Canvas.SetLeft(flipper, 52);
        Canvas.SetTop(flipper, 36);
        _body.Children.Add(flipper);
        // Two bent-elbow poses: the hand waves beside the cheek while the
        // upper flipper stays tucked in. Never rotate a straight arm upwards.
        Paint(_waveA, new[] { "..KK.", "..CCK", "..CCK", ".KCCK", "KCCC.", "CCC..", "CC...", "K...." });
        Paint(_waveB, new[] { "KK...", "CCK..", "KCCK.", ".CCCK", "KCCC.", "CCC..", "CC...", "K...." });
        foreach (var pose in new[] { _waveA, _waveB })
        {
            Canvas.SetLeft(pose, 52);
            Canvas.SetTop(pose, 20);
            _body.Children.Add(pose);
        }
        // Smiling eyes and a tiny beak-side grin replace the resting expression.
        var white = BrushFor('W');
        var dark = BrushFor('K');
        foreach (var x in new[] { 16.0, 40.0 })
        {
            Pixel(_happyFace, x, 20, 8, 8, white);
            Pixel(_happyFace, x, 22, 2, 4, dark);
            Pixel(_happyFace, x + 2, 20, 4, 2, dark);
            Pixel(_happyFace, x + 6, 22, 2, 4, dark);
        }
        Pixel(_happyFace, 26, 38, 2, 2, dark);
        Pixel(_happyFace, 28, 40, 8, 2, dark);
        Pixel(_happyFace, 36, 38, 2, 2, dark);
        _body.Children.Add(_happyFace);
        Content = new Viewbox { Width = 40, Height = 44, Child = drawing };
        Click += (_, _) => Greet();
        _greeting.Tick += (_, _) => UpdateAnimation();
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
            companion.Greet();
        else companion.UpdateAnimation();
    }

    private void StopAnimation()
    {
        _greeting.Stop();
        _flipper.BeginAnimation(RotateTransform.AngleProperty, null);
        _waveA.BeginAnimation(OpacityProperty, null);
        _waveB.BeginAnimation(OpacityProperty, null);
        _waveA.Opacity = 0;
        _waveB.Opacity = 0;
        _restingFlipper.Opacity = 1;
        _bounce.BeginAnimation(TranslateTransform.YProperty, null);
        _happyFace.Visibility = Visibility.Collapsed;
    }

    private void Greet()
    {
        StopAnimation();
        if (!IsLoaded || !IsVisible) return;
        _happyFace.Visibility = Visibility.Visible;
        _greeting.Start();
        if (!SystemParameters.ClientAreaAnimation) return;
        _restingFlipper.Opacity = 0;
        _waveA.Opacity = 1;
        _waveA.BeginAnimation(OpacityProperty, Frames(1, 0, 3));
        _waveB.BeginAnimation(OpacityProperty, Frames(0, 1, 3));
        _bounce.BeginAnimation(TranslateTransform.YProperty, Frames(0, -4, 3));
    }

    private void UpdateAnimation()
    {
        StopAnimation();
        if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation) return;
        if (IsBusy)
            _bounce.BeginAnimation(TranslateTransform.YProperty, Frames(0, -4, null));
    }

    // Discrete frames preserve the pixel grid, including during motion.
    private static DoubleAnimationUsingKeyFrames Frames(double from, double to, int? repeats) => new()
    {
        Duration = TimeSpan.FromSeconds(0.6),
        RepeatBehavior = repeats is { } count ? new RepeatBehavior(count) : RepeatBehavior.Forever,
        FillBehavior = FillBehavior.Stop,
        KeyFrames =
        {
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
            new DiscreteDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.2))),
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.4))),
        },
    };

    private static void Paint(Canvas canvas, string[] rows)
    {
        for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < rows[y].Length; x++)
                if (rows[y][x] != '.') Pixel(canvas, x * 4, y * 4, 4, 4, BrushFor(rows[y][x]));
    }

    private static Brush BrushFor(char pixel) => pixel switch
    {
        'K' => new SolidColorBrush(Color.FromRgb(0x35, 0x46, 0x5D)),
        'C' => new SolidColorBrush(Color.FromRgb(0x19, 0x25, 0x38)),
        'H' => new SolidColorBrush(Color.FromRgb(0x52, 0x6B, 0x86)),
        'W' => new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)),
        _ => new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x47)),
    };

    private static void Pixel(Canvas canvas, double x, double y, double width, double height, Brush fill)
    {
        var pixel = new Rectangle { Width = width, Height = height, Fill = fill };
        Canvas.SetLeft(pixel, x);
        Canvas.SetTop(pixel, y);
        canvas.Children.Add(pixel);
    }
}

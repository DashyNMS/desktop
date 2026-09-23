using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopNMS.Views;

/// <summary>A centred "working on it" card - spinner plus a line of text - for anything that loads before it can show content.</summary>
public partial class LoadingOverlay : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LoadingOverlay), new PropertyMetadata("Loading..."));

    private readonly DoubleAnimation _spin = new(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };

    public LoadingOverlay()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Passing null stops the animation - nothing spins while hidden.
        ArcRotation.BeginAnimation(RotateTransform.AngleProperty, IsVisible ? _spin : null);
    }
}

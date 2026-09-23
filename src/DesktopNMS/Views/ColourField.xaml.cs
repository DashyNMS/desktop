using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// A colour setting: a hex box plus a swatch. <see cref="Value"/> only
/// changes once the box holds a complete colour, so half-typed text never
/// reaches the map; the box keeps whatever's being typed meanwhile.
/// </summary>
public partial class ColourField : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ColourField), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColourField),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    private bool _updatingText;

    public ColourField()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (ColourField)d;
        var value = e.NewValue as string ?? string.Empty;

        // Don't overwrite what's being typed if it already means this colour.
        if (!string.Equals(CustomMapColours.TryNormalise(field.Input.Text), value, System.StringComparison.OrdinalIgnoreCase))
        {
            field._updatingText = true;
            field.Input.Text = value;
            field._updatingText = false;
        }

        field.UpdateSwatch(value);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText)
        {
            return;
        }

        if (CustomMapColours.TryNormalise(Input.Text) is { } colour)
        {
            Value = colour;
        }
    }

    private void UpdateSwatch(string value)
    {
        try
        {
            Swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
        catch (System.FormatException)
        {
            Swatch.Background = Brushes.Transparent;
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Maps;

/// <summary>
/// A colour setting: a hex box plus a swatch. <see cref="Value"/> only
/// changes once the box holds a complete colour, so half-typed text never
/// reaches the map; the box keeps whatever's being typed meanwhile.
/// </summary>
public partial class ColourField : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ColourField, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<ColourField, string>(nameof(Value), string.Empty, defaultBindingMode: BindingMode.TwoWay);

    private bool _updatingText;

    public ColourField()
    {
        InitializeComponent();
        Input.TextChanged += (_, _) =>
        {
            if (!_updatingText && CustomMapColours.TryNormalise(Input.Text) is { } colour)
            {
                Value = colour;
            }
        };
    }

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LabelProperty)
        {
            LabelText.Text = change.GetNewValue<string>();
        }
        else if (change.Property == ValueProperty)
        {
            var value = change.GetNewValue<string>() ?? string.Empty;

            // Don't overwrite what's being typed if it already means this colour.
            if (!string.Equals(CustomMapColours.TryNormalise(Input.Text), value, StringComparison.OrdinalIgnoreCase))
            {
                _updatingText = true;
                Input.Text = value;
                _updatingText = false;
            }

            Swatch.Background = Color.TryParse(value, out var colour) ? new SolidColorBrush(colour) : Brushes.Transparent;
        }
    }
}

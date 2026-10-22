using Avalonia;
using Avalonia.Controls;

namespace DesktopNMS.Views;

public partial class LoadingOverlay : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<LoadingOverlay, string?>(nameof(Text), "Loading…");

    public LoadingOverlay()
    {
        InitializeComponent();
    }

    /// <summary>What's being waited for: "Loading rules…".</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}

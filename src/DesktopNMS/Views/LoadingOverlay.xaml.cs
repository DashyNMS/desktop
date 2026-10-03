using System.Windows;
using System.Windows.Controls;

namespace DesktopNMS.Views;

/// <summary>A centred "working on it" card - the logo's heartbeat over a line of text (#228) - for anything that loads before it can show content.</summary>
public partial class LoadingOverlay : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LoadingOverlay), new PropertyMetadata("Loading…"));

    public LoadingOverlay()
    {
        InitializeComponent();

        // It beats while it shows - and the logo stops by itself while hidden.
        Logo.IsBeating = true;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}

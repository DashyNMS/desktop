using Avalonia;
using Avalonia.Controls;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class NotPortedView : UserControl
{
    public static readonly StyledProperty<MainTab> PageProperty = AvaloniaProperty.Register<NotPortedView, MainTab>(nameof(Page));

    public NotPortedView()
    {
        InitializeComponent();
        PageName.Text = Describe(Page);
    }

    public MainTab Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty)
        {
            PageName.Text = Describe(Page);
        }
    }

    /// <summary>The page as the sidebar names it.</summary>
    private static string Describe(MainTab page) => page switch
    {
        MainTab.MapsNetwork => "Network map",
        MainTab.MapsGeographical => "Geographical map",
        MainTab.MapsCustom => "Custom maps",
        MainTab.LogsGraylog => "Graylog",
        MainTab.LogsEvents => "Event log",
        MainTab.LogsAlerts => "Alert log",
        MainTab.Alerts => "Active alerts",
        _ => page.ToString(),
    };
}

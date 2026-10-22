using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;
    private readonly List<Flyout> _sectionFlyouts = new();
    private StackPanel? _captionButtons;

    /// <summary>For the designer.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel, ISettingsStore settings)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        WindowPlacementMemory.Attach(this, settings);

        // Room for the OS's own window controls: macOS's traffic lights on the
        // left, the window buttons on the right - measured once they're drawn
        // (see LayoutUpdated). Linux window managers draw their own title bar
        // above this one.
        TitleBarContent.Margin = OperatingSystem.IsMacOS() ? new Thickness(80, 0, 12, 0)
            : OperatingSystem.IsWindows() ? new Thickness(19, 0, 146, 0)
            : new Thickness(19, 0, 12, 0);
        LayoutUpdated += (_, _) => FitTitleBarToWindowButtons();

        // Narrow, a section's sub-pages open in a flyout to its right on hover (#118).
        foreach (var section in new[] { DevicesSection, NeighboursSection, MapsSection, LogsSection, AlertsSection })
        {
            if (FlyoutBase.GetAttachedFlyout(section) is not Flyout flyout)
            {
                continue;
            }

            // The sidebar stays live under an open flyout: the section keeps its
            // highlight and opens its page on a click, and the next section can
            // open its own flyout. A click anywhere else closes it and still acts.
            flyout.OverlayInputPassThroughElement = Rail;
            flyout.OverlayDismissEventPassThrough = true;
            _sectionFlyouts.Add(flyout);

            section.PointerEntered += (_, _) =>
            {
                if (_viewModel is { IsNavExpanded: false } && !flyout.IsOpen)
                {
                    HideSectionFlyouts();
                    FlyoutBase.ShowAttachedFlyout(section);
                }
            };
            section.Click += (_, _) => HideSectionFlyouts();

            // Picking a page from the flyout closes it - once the click is done:
            // a button's Click comes before its Command, and closing the flyout
            // there takes the button out of the window, unbinding the Command
            // before it runs.
            if (flyout.Content is Control content)
            {
                content.AddHandler(Button.ClickEvent, (_, _) => Dispatcher.UIThread.Post(flyout.Hide), RoutingStrategies.Bubble);
            }
        }
    }

    private void HideSectionFlyouts()
    {
        foreach (var flyout in _sectionFlyouts.Where(f => f.IsOpen))
        {
            flyout.Hide();
        }
    }

    /// <summary>
    /// Keeps the title bar's own buttons clear of the window buttons Avalonia
    /// draws over its right-hand end, whatever their number and size.
    /// </summary>
    private void FitTitleBarToWindowButtons()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        _captionButtons ??= this.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "PART_OverlayPanel");
        if (_captionButtons is not { IsVisible: true } buttons)
        {
            return;
        }

        var right = buttons.Bounds.Width + 8;
        if (right > 8 && Math.Abs(TitleBarContent.Margin.Right - right) > 0.5)
        {
            var margin = TitleBarContent.Margin;
            TitleBarContent.Margin = new Thickness(margin.Left, margin.Top, right, margin.Bottom);
        }
    }
}

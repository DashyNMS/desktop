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

        TitleBarFit.Attach(this, TitleBarContent, left: 19);

        // Ctrl+F (Cmd+F on a Mac) to the showing tab's search box - tunnelling,
        // so it's seen before a focused text box treats it as typing.
        AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);

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

    private void OnFindKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.F
            || (e.KeyModifiers != Avalonia.Input.KeyModifiers.Control && e.KeyModifiers != Avalonia.Input.KeyModifiers.Meta)
            || _viewModel is not { } viewModel)
        {
            return;
        }

        Action? focus = viewModel switch
        {
            { IsDevicesTabSelected: true } => DevicesViewControl.FocusSearch,
            { IsHealthTabSelected: true } => HealthViewControl.FocusSearch,
            { IsAlertsTabSelected: true } => AlertsViewControl.FocusSearch,
            { IsGroupsTabSelected: true } => GroupsViewControl.FocusSearch,
            { IsLocationsTabSelected: true } => LocationsViewControl.FocusSearch,
            { IsNeighboursTabSelected: true } => NeighboursViewControl.FocusSearch,
            { IsNetworkMapTabSelected: true } => NetworkMapViewControl.FocusSearch,
            { IsGeoMapTabSelected: true } => GeoMapViewControl.FocusSearch,
            { IsGraylogLogsTabSelected: true } => LogsViewControl.FocusSearch,

            // The Dashboard has no search box yet.
            _ => null,
        };

        if (focus is not null)
        {
            focus();
            e.Handled = true;
        }
    }

    private void HideSectionFlyouts()
    {
        foreach (var flyout in _sectionFlyouts.Where(f => f.IsOpen))
        {
            flyout.Hide();
        }
    }
}

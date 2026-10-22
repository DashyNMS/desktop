using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// The Avalonia app's <see cref="IWindowService"/> (#250): the main window,
/// sign-in and the themed dialogs. Windows not ported yet say so rather than
/// failing - each page's own PR replaces its line here.
/// </summary>
public sealed class WindowService(IServiceProvider services, ILogger<WindowService> logger) : IWindowService
{
    private static readonly string[] ExternalToolSchemes = ["ssh", "telnet", "rdp", "vnc", "http", "https"];

    private MainWindow? _mainWindow;

    /// <summary>Raised by Exit - the app decides how to close.</summary>
    public event EventHandler? ExitRequested;

    public void AttachMainWindow(MainWindow window) => _mainWindow = window;

    private MainViewModel Main => services.GetRequiredService<MainViewModel>();

    // ------------------------------------------------------------ main window

    public void ShowMain()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    public void HideMain() => _mainWindow?.Hide();

    public void ShowMainTab(MainTab tab)
    {
        Main.SelectedTab = tab;
        ShowMain();
    }

    public void ShowDevicesTab()
    {
        ShowMain();
        Main.SelectDevicesTabCommand.Execute(null);
    }

    public void ShowAlertsForDevice(int deviceId, string deviceName)
    {
        Main.ShowAlertsForDevice(deviceId, deviceName);
        ShowMain();
    }

    public void ShowAlertsWithSeverity(AlertSeverity? severity)
    {
        Main.ShowAlertsWithSeverity(severity);
        ShowMain();
    }

    public void ShowDevicesFilteredByLocation(string location)
    {
        services.GetRequiredService<DeviceListViewModel>().FilterByLocationOnly(location);
        ShowDevicesTab();
    }

    public void ShowDevicesFilteredByLocations(IReadOnlyCollection<string> locations)
    {
        services.GetRequiredService<DeviceListViewModel>().FilterByLocationsOnly(locations);
        ShowDevicesTab();
    }

    public void ShowDevicesFilteredByGroup(string groupName)
    {
        services.GetRequiredService<DeviceListViewModel>().FilterByGroupOnly(groupName);
        ShowDevicesTab();
    }

    public void ShowDevicesWithState(DeviceState state)
    {
        services.GetRequiredService<DeviceListViewModel>().FilterByStateOnly(state);
        ShowDevicesTab();
    }

    public void ShowNeighbour(string viewId, string? name, string? mac = null)
    {
        ShowMain();
        Main.SelectNeighboursTabCommand.Execute(null);
        Main.Neighbours.Show(viewId, name, mac);
    }

    public void ShowGraylogMessage(GraylogMessage message)
    {
        Main.SelectGraylogTabCommand.Execute(null);
        services.GetRequiredService<LogsViewModel>().Graylog.ShowMessage(message);
        ShowMain();
    }

    public void AddPortGraphToDashboard(int deviceId, string ifName, string graphType, string title)
        => services.GetRequiredService<IDashboardLayoutService>().AddPortGraph(deviceId, ifName, graphType, title);

    public void ShowSettingsSection(SettingsSection section)
    {
        ShowMain();
        Main.OpenSettingsAt(section);
    }

    public void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    // --------------------------------------------------------------- sign-in

    public bool ShowSignInDialog()
    {
        var window = new ConnectionWindow(services.GetRequiredService<ConnectionViewModel>());
        Modal.Show(window, Owner());
        return window.Succeeded;
    }

    // --------------------------------------------------------- settings, about

    public bool ShowSettingsDialog(SettingsSection? section = null)
    {
        var viewModel = services.GetRequiredService<SettingsViewModel>();
        if (section is { } open)
        {
            viewModel.SelectedSection = open;
        }

        var window = new SettingsWindow(viewModel);
        WindowPlacementMemory.Attach(window, services.GetRequiredService<ISettingsStore>());
        Modal.Show(window, Owner());
        return window.Saved;
    }

    public bool ShowWhatsNew()
    {
        if (BundledWhatsNew.Current is not { } notes)
        {
            return false;
        }

        var window = new WhatsNewWindow { DataContext = new WhatsNewViewModel(notes, new Uri(UpdateReleases.ReleasePageUrl(notes.Version)), OpenUrl) };
        Modal.Show(window, Owner());

        // However it was closed, these notes have been seen.
        var settings = services.GetRequiredService<ISettingsStore>();
        settings.Current.WhatsNewShownVersion = notes.Version;
        settings.SaveQuietly();
        return true;
    }

    public void ShowLicences()
        => Modal.Show(new LicencesWindow { DataContext = new LicencesViewModel(OpenUrl) }, Owner());

    // --------------------------------------------------------------- dialogs

    public void ShowError(string title, string message) => ShowNotice(title, message, isError: true);

    public void ShowInformation(string title, string message) => ShowNotice(title, message, isError: false);

    public bool Confirm(string title, string message, string confirmLabel, bool destructive = false)
        => ShowConfirm(new ConfirmDialogViewModel(title, message, showDontAskAgain: false, dontAskAgainLabel: string.Empty, confirmLabel: confirmLabel) { IsDestructive = destructive });

    public (bool Confirmed, bool DontAskAgain) ConfirmWithOptOut(string title, string message, string confirmLabel, string dontAskAgainLabel = "Don't ask me again")
    {
        var viewModel = new ConfirmDialogViewModel(title, message, showDontAskAgain: true, dontAskAgainLabel, confirmLabel: confirmLabel);
        var confirmed = ShowConfirm(viewModel);
        return (confirmed, viewModel.DontAskAgain);
    }

    public bool ConfirmTrustCertificate(string service, CertificateDetails certificate)
    {
        var (title, message) = CertificateTrust.DescribeForPrompt(certificate, service);
        return ShowConfirm(new ConfirmDialogViewModel(title, message, showDontAskAgain: false, dontAskAgainLabel: string.Empty, confirmLabel: "Trust this certificate", isError: certificate.ReplacesTrustedCertificate));
    }

    private void ShowNotice(string title, string message, bool isError)
        => ShowConfirm(new ConfirmDialogViewModel(title, message, showDontAskAgain: false, dontAskAgainLabel: string.Empty, showCancel: false, confirmLabel: "OK", isError: isError));

    private bool ShowConfirm(ConfirmDialogViewModel viewModel)
    {
        var window = new ConfirmDialog(viewModel);
        Modal.Show(window, Owner());
        return window.Confirmed;
    }

    /// <summary>The window a dialog belongs over: whichever DashyNMS window is active, else the main one.</summary>
    private Window? Owner()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible) ?? (_mainWindow is { IsVisible: true } ? _mainWindow : null)
            : null;

    // --------------------------------------------------------------- outside

    public void OpenUrl(Uri url)
    {
        // Only ever open http/https: the URL comes from the LibreNMS server, so
        // it is not something to hand to the OS unchecked.
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogWarning("Refused to open a non-web URL: {Url}", url);
            return;
        }

        Launch(url, "Could not open the browser");
    }

    public void OpenExternalTool(Uri uri)
    {
        if (!ExternalToolSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("Refused to open an unsupported external tool scheme: {Uri}", uri);
            return;
        }

        Launch(uri, "Could not open");
    }

    private void Launch(Uri uri, string failureTitle)
    {
        var launcher = (Owner() ?? _mainWindow)?.Launcher;
        if (launcher is null)
        {
            return;
        }

        _ = LaunchAsync();

        async Task LaunchAsync()
        {
            try
            {
                if (!await launcher.LaunchUriAsync(uri).ConfigureAwait(true))
                {
                    ShowError(failureTitle, $"Nothing on {PlatformText.ThisComputer} opens {uri.Scheme}:// links.");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not open {Uri}", uri);
                ShowError(failureTitle, ex.Message);
            }
        }
    }

    // ------------------------------------------------- not in this preview yet

    private void NotYet(string what)
    {
        logger.LogInformation("Not ported to the cross-platform app yet: {What}", what);
        ShowInformation("Not here yet", $"{what} isn't in the cross-platform preview yet. It's being ported (#250); the Windows app has it.");
    }

    // ------------------------------------------------------------ device windows

    /// <summary>One window per device: opening a device that's already open brings its window forward.</summary>
    private readonly Dictionary<int, DeviceView> _openDeviceWindows = new();

    public void ShowDeviceDetail(int deviceId)
    {
        if (_openDeviceWindows.TryGetValue(deviceId, out var existing))
        {
            existing.Activate();
            return;
        }

        var settings = services.GetRequiredService<ISettingsStore>();
        var history = new DeviceBrowseHistory(deviceId);
        var window = new DeviceView(CreateDeviceDetailViewModel(deviceId, history), settings);
        DeviceWindowTaskbar.Attach(window, _mainWindow);

        // Back, forward and the breadcrumbs (#58) move this same window.
        history.NavigateRequested += (_, index) => NavigateDeviceWindow(window, history, history.Entries[index].DeviceId, index);

        window.Closed += (_, _) =>
        {
            // Whichever device the window ended up on.
            foreach (var key in _openDeviceWindows.Where(kv => ReferenceEquals(kv.Value, window)).Select(kv => kv.Key).ToList())
            {
                _openDeviceWindows.Remove(key);
            }

            if (window.DataContext is DeviceDetailViewModel current)
            {
                current.OpenDeviceRequested -= OnOpenDeviceRequested;
                current.Dispose();
            }
        };

        _openDeviceWindows[deviceId] = window;
        // All device windows belong to the main window, even when opened from
        // another device. Nesting owners would close one device with another.
        if (_mainWindow is { IsVisible: true } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    private DeviceDetailViewModel CreateDeviceDetailViewModel(int deviceId, DeviceBrowseHistory history)
    {
        var viewModel = new DeviceDetailViewModel(
            deviceId,
            services.GetRequiredService<DeviceMonitor>(),
            services.GetRequiredService<SensorMonitor>(),
            services.GetRequiredService<AlertMonitor>(),
            services.GetRequiredService<IDeviceCache>(),
            services.GetRequiredService<ILibreNmsClient>(),
            services.GetRequiredService<IAlertRuleCache>(),
            services.GetRequiredService<ISessionService>(),
            services.GetRequiredService<ISettingsStore>(),
            this,
            services.GetRequiredService<IUnimusApi>(),
            services.GetRequiredService<IUnimusDeviceResolver>(),
            services.GetRequiredService<IGraylogApi>(),
            services.GetRequiredService<IFleetLinks>(),
            services.GetRequiredService<ILogger<DeviceDetailViewModel>>())
        {
            History = history,
        };

        viewModel.OpenDeviceRequested += OnOpenDeviceRequested;
        return viewModel;
    }

    /// <summary>A link in a device window (a neighbour) opens that device in the same window, with the way back.</summary>
    private void OnOpenDeviceRequested(object? sender, int deviceId)
    {
        if (sender is DeviceDetailViewModel { History: { } history } source
            && _openDeviceWindows.TryGetValue(source.DeviceId, out var window))
        {
            NavigateDeviceWindow(window, history, deviceId, historyIndex: null);
        }
        else
        {
            ShowDeviceDetail(deviceId);
        }
    }

    /// <summary>
    /// Moves a device window to another device (#58): a new stop in its history,
    /// or back or forward to one it's been to, on the section it was left on. A
    /// device already open in another window is brought forward there instead.
    /// </summary>
    private void NavigateDeviceWindow(DeviceView window, DeviceBrowseHistory history, int deviceId, int? historyIndex)
    {
        if (_openDeviceWindows.TryGetValue(deviceId, out var other) && !ReferenceEquals(other, window))
        {
            other.Activate();
            return;
        }

        if (window.DataContext is not DeviceDetailViewModel previous || previous.DeviceId == deviceId)
        {
            return;
        }

        history.UpdateCurrent(previous.Name, previous.SelectedSection);
        if (historyIndex is { } index)
        {
            history.GoTo(index);
        }
        else
        {
            history.Visit(deviceId);
        }

        var next = CreateDeviceDetailViewModel(deviceId, history);
        if (history.Current.Section != DeviceDetailSection.Overview)
        {
            next.SelectedSection = history.Current.Section;
        }

        _openDeviceWindows.Remove(previous.DeviceId);
        _openDeviceWindows[deviceId] = window;
        window.ShowViewModel(next);

        previous.OpenDeviceRequested -= OnOpenDeviceRequested;
        previous.Dispose();
    }

    /// <summary>Opens a device (or brings its window forward) and acts on its view model there.</summary>
    private void ShowDevice(int deviceId, Action<DeviceDetailViewModel> then)
    {
        ShowDeviceDetail(deviceId);
        if (_openDeviceWindows.TryGetValue(deviceId, out var window) && window.DataContext is DeviceDetailViewModel viewModel)
        {
            then(viewModel);
        }
    }

    public void ShowDeviceGraph(int deviceId, string graphName) => ShowDevice(deviceId, vm => vm.ShowGraph(graphName));

    public void ShowDevicePortGraph(int deviceId, string ifName, string graphType) => ShowDevice(deviceId, vm => vm.ShowPortGraph(ifName, graphType));

    public void ShowDeviceWireless(int deviceId) => ShowDevice(deviceId, vm => vm.SelectWirelessCommand.Execute(null));

    public void ShowDevicePort(int deviceId, int portId) => ShowDevice(deviceId, vm => vm.ShowPort(portId));

    public void ShowDeviceEventLog(int deviceId, int? eventId = null) => ShowDevice(deviceId, vm =>
    {
        vm.SelectEventLogCommand.Execute(null);
        if (eventId is { } id)
        {
            vm.ShowEventLogEntry(id);
        }
    });

    public void ShowDeviceAlerts(int deviceId, int? alertLogId = null) => ShowDevice(deviceId, vm =>
    {
        vm.SelectAlertsCommand.Execute(null);
        if (alertLogId is { } id)
        {
            vm.ShowAlertLogEntry(id);
        }
    });

    public void ShowDeviceGraylog(int deviceId, GraylogMessage? message = null) => ShowDevice(deviceId, vm =>
    {
        vm.SelectGraylogCommand.Execute(null);
        if (message is not null)
        {
            vm.Graylog.ShowMessage(message);
        }
    });

    public void CloseDeviceDetail(int deviceId)
    {
        if (_openDeviceWindows.TryGetValue(deviceId, out var window))
        {
            window.Close();
        }
    }


    public IReadOnlyList<WidgetCatalogEntry> ShowWidgetPicker(IReadOnlyList<WidgetCatalogEntry> catalog)
    {
        var viewModel = new WidgetPickerViewModel(catalog);
        var window = new WidgetPickerWindow(viewModel);
        WindowPlacementMemory.Attach(window, services.GetRequiredService<ISettingsStore>());
        Modal.Show(window, Owner());
        return window.Added ? viewModel.Chosen : [];
    }


    public NeighbourViewDefinition? ShowNeighbourViewEditor(NeighbourViewDefinition? existing)
    {
        var viewModel = services.GetRequiredService<NeighbourViewEditorViewModel>();
        if (existing is not null)
        {
            viewModel.Initialize(existing);
        }

        var window = new NeighbourViewEditorWindow(viewModel);
        Modal.Show(window, Owner());
        return window.Saved ? viewModel.Result : null;
    }


    /// <summary>Live: DeviceListViewModel is a singleton, so its filters are the ones filtering the grid.</summary>
    public void ShowDeviceFiltersDialog()
    {
        var window = new DeviceFiltersWindow { DataContext = services.GetRequiredService<DeviceListViewModel>() };
        WindowPlacementMemory.Attach(window, services.GetRequiredService<ISettingsStore>());
        Modal.Show(window, Owner());
    }

    /// <summary>Live: MainViewModel is a singleton, so its GroupFilter is the one filtering the grid.</summary>
    public void ShowAlertFiltersDialog()
    {
        var window = new AlertFiltersWindow { DataContext = services.GetRequiredService<MainViewModel>() };
        WindowPlacementMemory.Attach(window, services.GetRequiredService<ISettingsStore>());
        Modal.Show(window, Owner());
    }

    public bool ShowAddDeviceDialog()
    {
        var window = new AddDeviceWindow(services.GetRequiredService<AddDeviceViewModel>());
        WindowPlacementMemory.Attach(window, services.GetRequiredService<ISettingsStore>());
        Modal.Show(window, Owner());

        // "Add several…" closes it and opens Bulk add devices in its place.
        return window.SwitchToBulkAdd ? ShowBulkAddDevicesDialog() : window.Added;
    }

    public bool ShowBulkAddDevicesDialog()
    {
        var viewModel = services.GetRequiredService<BulkAddDevicesViewModel>();
        var window = new BulkAddDevicesWindow(viewModel);
        WindowPlacementMemory.Attach(window, services.GetRequiredService<ISettingsStore>());
        Modal.Show(window, Owner());
        return viewModel.AnyAdded;
    }

    public bool ShowAddDeviceGroupDialog() => ShowDeviceGroupEditor(services.GetRequiredService<DeviceGroupEditorViewModel>());

    public bool ShowEditDeviceGroupDialog(DeviceGroup group)
    {
        var viewModel = services.GetRequiredService<DeviceGroupEditorViewModel>();
        viewModel.Initialize(group);
        return ShowDeviceGroupEditor(viewModel);
    }

    private bool ShowDeviceGroupEditor(DeviceGroupEditorViewModel viewModel)
    {
        var window = new DeviceGroupEditorWindow(viewModel);
        Modal.Show(window, Owner());
        return window.Saved;
    }

    public bool ShowAddDevicesToGroupDialog(IReadOnlyList<int> deviceIds)
    {
        var viewModel = services.GetRequiredService<AddDevicesToGroupViewModel>();
        viewModel.Initialize(deviceIds);
        var window = new AddDevicesToGroupWindow(viewModel);
        Modal.Show(window, Owner());
        return window.Saved;
    }

    public bool ShowAddLocationDialog() => ShowLocationEditor(services.GetRequiredService<LocationEditorViewModel>());

    public bool ShowEditLocationDialog(DesktopNMS.Core.Models.Location location)
    {
        var viewModel = services.GetRequiredService<LocationEditorViewModel>();
        viewModel.Initialize(location);
        return ShowLocationEditor(viewModel);
    }

    private bool ShowLocationEditor(LocationEditorViewModel viewModel)
    {
        var window = new LocationEditorWindow(viewModel);
        Modal.Show(window, Owner());
        return window.Saved;
    }

    public bool ShowAddRuleDialog() => ShowRuleEditor(services.GetRequiredService<RuleEditorViewModel>());

    public bool ShowEditRuleDialog(AlertRule rule)
    {
        var viewModel = services.GetRequiredService<RuleEditorViewModel>();
        viewModel.Initialize(rule);
        return ShowRuleEditor(viewModel);
    }

    public bool ShowDuplicateRuleDialog(AlertRule rule)
    {
        var viewModel = services.GetRequiredService<RuleEditorViewModel>();
        viewModel.InitializeAsCopy(rule);
        return ShowRuleEditor(viewModel);
    }

    private bool ShowRuleEditor(RuleEditorViewModel viewModel)
    {
        var window = new RuleEditorWindow(viewModel);
        WindowPlacementMemory.Attach(window, services.GetRequiredService<ISettingsStore>());
        Modal.Show(window, Owner());
        return window.Saved;
    }

    public string? ShowScheduleMaintenanceDialog(int deviceId, string deviceName) => ShowScheduleMaintenanceDialog([new MaintenanceTarget(deviceId, deviceName)]);

    /// <summary>What was scheduled, to confirm, or nothing if it was cancelled.</summary>
    public string? ShowScheduleMaintenanceDialog(IReadOnlyList<MaintenanceTarget> devices)
    {
        var viewModel = services.GetRequiredService<MaintenanceScheduleViewModel>();
        viewModel.Initialize(devices);
        var window = new MaintenanceScheduleWindow(viewModel);
        Modal.Show(window, Owner());
        return window.Saved ? viewModel.ConfirmationMessage : null;
    }

    public bool ShowAddAlertTemplateDialog() => ShowAlertTemplateEditor(services.GetRequiredService<AlertTemplateEditorViewModel>());

    public bool ShowEditAlertTemplateDialog(AlertTemplate template)
    {
        var viewModel = services.GetRequiredService<AlertTemplateEditorViewModel>();
        viewModel.Initialize(template);
        return ShowAlertTemplateEditor(viewModel);
    }

    private bool ShowAlertTemplateEditor(AlertTemplateEditorViewModel viewModel)
    {
        var window = new AlertTemplateEditorWindow(viewModel);
        WindowPlacementMemory.Attach(window, services.GetRequiredService<ISettingsStore>());
        Modal.Show(window, Owner());
        return window.Saved;
    }

    private bool NotNow(string what)
    {
        NotYet(what);
        return false;
    }
}

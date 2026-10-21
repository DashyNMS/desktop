using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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

    public void ShowDeviceDetail(int deviceId) => NotYet("Device Details");

    public void ShowDeviceGraph(int deviceId, string graphName) => NotYet("Device Details");

    public void ShowDevicePortGraph(int deviceId, string ifName, string graphType) => NotYet("Device Details");

    public void ShowDeviceWireless(int deviceId) => NotYet("Device Details");

    public void ShowDevicePort(int deviceId, int portId) => NotYet("Device Details");

    public void ShowDeviceEventLog(int deviceId, int? eventId = null) => NotYet("Device Details");

    public void ShowDeviceAlerts(int deviceId, int? alertLogId = null) => NotYet("Device Details");

    public void ShowDeviceGraylog(int deviceId, GraylogMessage? message = null) => NotYet("Device Details");

    public void CloseDeviceDetail(int deviceId)
    {
    }


    public IReadOnlyList<WidgetCatalogEntry> ShowWidgetPicker(IReadOnlyList<WidgetCatalogEntry> catalog)
    {
        NotYet("Adding dashboard widgets");
        return [];
    }


    public NeighbourViewDefinition? ShowNeighbourViewEditor(NeighbourViewDefinition? existing)
    {
        NotYet("The neighbourhood editor");
        return null;
    }


    public void ShowDeviceFiltersDialog() => NotYet("Device filters");

    public void ShowAlertFiltersDialog() => NotYet("Alert filters");

    public bool ShowAddDeviceDialog() => NotNow("Adding a device");

    public bool ShowBulkAddDevicesDialog() => NotNow("Adding devices in bulk");

    public bool ShowAddDeviceGroupDialog() => NotNow("The device group editor");

    public bool ShowEditDeviceGroupDialog(DeviceGroup group) => NotNow("The device group editor");

    public bool ShowAddDevicesToGroupDialog(IReadOnlyList<int> deviceIds) => NotNow("Adding devices to a group");

    public bool ShowAddLocationDialog() => NotNow("The location editor");

    public bool ShowEditLocationDialog(DesktopNMS.Core.Models.Location location) => NotNow("The location editor");

    public bool ShowAddRuleDialog() => NotNow("The rule editor");

    public bool ShowEditRuleDialog(AlertRule rule) => NotNow("The rule editor");

    public bool ShowDuplicateRuleDialog(AlertRule rule) => NotNow("The rule editor");

    public string? ShowScheduleMaintenanceDialog(int deviceId, string deviceName)
    {
        NotYet("Scheduling maintenance");
        return null;
    }

    public string? ShowScheduleMaintenanceDialog(IReadOnlyList<MaintenanceTarget> devices)
    {
        NotYet("Scheduling maintenance");
        return null;
    }

    public bool ShowAddAlertTemplateDialog() => NotNow("The alert template editor");

    public bool ShowEditAlertTemplateDialog(AlertTemplate template) => NotNow("The alert template editor");

    private bool NotNow(string what)
    {
        NotYet(what);
        return false;
    }
}

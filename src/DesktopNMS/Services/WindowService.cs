using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>WPF implementation of <see cref="IWindowService"/>.</summary>
public sealed class WindowService : IWindowService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WindowService> _logger;
    private readonly Dictionary<int, DeviceView> _openDeviceWindows = new();

    private MainWindow? _mainWindow;

    public WindowService(IServiceProvider services, ILogger<WindowService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>Called by the app once the main window exists.</summary>
    public void AttachMainWindow(MainWindow window) => _mainWindow = window;

    /// <summary>
    /// Makes the main window a window's owner, and brings the main window back
    /// when it closes if the main window was minimised and nothing else is
    /// left on screen (#181) - Windows activates an owner but never restores it.
    /// </summary>
    /// <summary>Reopens a resizable window how it was last left (#59) - see <see cref="WindowPlacementMemory"/>.</summary>
    private void RememberPlacement(Window window) =>
        WindowPlacementMemory.Attach(window, _services.GetRequiredService<ISettingsStore>());

    private void OwnByMain(Window window)
    {
        window.Owner = _mainWindow;
        window.Closed += (_, _) =>
        {
            if (_mainWindow is not { IsVisible: true, WindowState: WindowState.Minimized } main)
            {
                return;
            }

            var othersOnScreen = Application.Current.Windows
                .OfType<Window>()
                .Any(w => !ReferenceEquals(w, main) && !ReferenceEquals(w, window) && w.IsVisible && w.WindowState != WindowState.Minimized);
            if (!othersOnScreen)
            {
                main.RestoreFromMinimised();
            }
        };
    }

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
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    public void HideMain() => _mainWindow?.Hide();

    public void ShowDevicesTab()
    {
        ShowMain();
        _services.GetRequiredService<MainViewModel>().SelectDevicesTabCommand.Execute(null);
    }

    public void ShowAlertsForDevice(int deviceId, string deviceName)
    {
        _services.GetRequiredService<MainViewModel>().ShowAlertsForDevice(deviceId, deviceName);
        ShowMain();
    }

    public void ShowDeviceGraph(int deviceId, string graphName)
    {
        ShowDeviceDetail(deviceId);

        if (_openDeviceWindows.TryGetValue(deviceId, out var window)
            && window.DataContext is DeviceDetailViewModel viewModel)
        {
            viewModel.ShowGraph(graphName);
        }
    }

    public void ShowNeighbour(string viewId, string? name, string? mac = null)
    {
        ShowMain();
        var main = _services.GetRequiredService<MainViewModel>();
        main.SelectNeighboursTabCommand.Execute(null);
        main.Neighbours.Show(viewId, name, mac);
    }

    public NeighbourViewDefinition? ShowNeighbourViewEditor(NeighbourViewDefinition? existing)
    {
        var viewModel = _services.GetRequiredService<NeighbourViewEditorViewModel>();
        if (existing is not null)
        {
            viewModel.Initialize(existing);
        }

        var window = new NeighbourViewEditorWindow(viewModel);
        RememberPlacement(window);
        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        return window.ShowDialog() == true ? viewModel.Result : null;
    }

    public void ShowDeviceWireless(int deviceId)
    {
        ShowDeviceDetail(deviceId);

        if (_openDeviceWindows.TryGetValue(deviceId, out var window)
            && window.DataContext is DeviceDetailViewModel viewModel)
        {
            viewModel.SelectWirelessCommand.Execute(null);
        }
    }

    public void ShowDeviceDetail(int deviceId)
    {
        if (_openDeviceWindows.TryGetValue(deviceId, out var existing))
        {
            existing.Activate();
            return;
        }

        var settings = _services.GetRequiredService<ISettingsStore>();
        var history = new DeviceBrowseHistory(deviceId);
        var viewModel = CreateDeviceDetailViewModel(deviceId, history);

        var window = new DeviceView(viewModel, settings);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

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
        window.Show();
    }

    private DeviceDetailViewModel CreateDeviceDetailViewModel(int deviceId, DeviceBrowseHistory history)
    {
        var viewModel = new DeviceDetailViewModel(
            deviceId,
            _services.GetRequiredService<DeviceMonitor>(),
            _services.GetRequiredService<SensorMonitor>(),
            _services.GetRequiredService<AlertMonitor>(),
            _services.GetRequiredService<IDeviceCache>(),
            _services.GetRequiredService<ILibreNmsClient>(),
            _services.GetRequiredService<IAlertRuleCache>(),
            _services.GetRequiredService<ISessionService>(),
            _services.GetRequiredService<ISettingsStore>(),
            this,
            _services.GetRequiredService<IUnimusApi>(),
            _services.GetRequiredService<IUnimusDeviceResolver>(),
            _services.GetRequiredService<IGraylogApi>(),
            _services.GetRequiredService<IFleetLinks>(),
            _services.GetRequiredService<ILogger<DeviceDetailViewModel>>())
        {
            History = history,
        };

        viewModel.OpenDeviceRequested += OnOpenDeviceRequested;
        return viewModel;
    }

    /// <summary>A link in a device window (a neighbour) - open that device in the same window, with the way back.</summary>
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
    /// Switches a device window to another device (#58): a new stop in its
    /// history, or (<paramref name="historyIndex"/>) back or forward to one it
    /// has already been to - reopening on the section it was left on. One
    /// window per device still: a device already open in another window is
    /// brought to the front there instead.
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

    public void CloseDeviceDetail(int deviceId)
    {
        if (_openDeviceWindows.TryGetValue(deviceId, out var window))
        {
            window.Close();
        }
    }

    public void ShowDevicesFilteredByLocation(string location)
    {
        _services.GetRequiredService<DeviceListViewModel>().FilterByLocationOnly(location);
        ShowDevicesTab();
    }

    public void ShowDevicesFilteredByLocations(IReadOnlyCollection<string> locations)
    {
        _services.GetRequiredService<DeviceListViewModel>().FilterByLocationsOnly(locations);
        ShowDevicesTab();
    }

    public void ShowDevicesFilteredByGroup(string groupName)
    {
        _services.GetRequiredService<DeviceListViewModel>().FilterByGroupOnly(groupName);
        ShowDevicesTab();
    }

    public bool ShowSettingsDialog()
    {
        var viewModel = _services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        return window.ShowDialog() == true;
    }

    public void ShowDeviceFiltersDialog()
    {
        // DeviceListViewModel is a singleton (see App.xaml.cs), so this
        // resolves the exact same instance already driving the Devices tab -
        // its TypeFilter/LocationFilter/GroupFilter are the same live
        // objects, not a copy, so checking a box here immediately affects
        // the device grid behind this dialog.
        var viewModel = _services.GetRequiredService<DeviceListViewModel>();
        var window = new DeviceFiltersWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        window.ShowDialog();
    }

    public void ShowAlertFiltersDialog()
    {
        // Same live-object reasoning as ShowDeviceFiltersDialog: MainViewModel
        // is a singleton, so its GroupFilter here is the one filtering the grid.
        var window = new AlertFiltersWindow(_services.GetRequiredService<MainViewModel>());
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        window.ShowDialog();
    }

    public bool ShowSignInDialog()
    {
        var viewModel = _services.GetRequiredService<ConnectionViewModel>();
        var window = new ConnectionWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        return window.ShowDialog() == true;
    }

    public void OpenUrl(Uri url)
    {
        // Only ever open http/https: the URL comes from the LibreNMS server, so
        // it is not something to hand to the shell unchecked.
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("Refused to open a non-web URL: {Url}", url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open {Url}", url);
            ShowError("Could not open the browser", ex.Message);
        }
    }

    private static readonly string[] ExternalToolSchemes = { Uri.UriSchemeHttp, Uri.UriSchemeHttps, "telnet", "ssh" };

    public void OpenExternalTool(Uri uri)
    {
        if (!ExternalToolSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refused to open an unsupported external tool scheme: {Uri}", uri);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open external tool for {Uri}", uri);
            ShowError(
                "Could not open",
                uri.Scheme is "telnet" or "ssh"
                    ? $"No application is registered to handle {uri.Scheme}:// links. Install a client (e.g. PuTTY) that registers one, or enable Windows' Telnet Client feature."
                    : ex.Message);
        }
    }

    public void ShowError(string title, string message) => ShowNotice(title, message, isError: true);

    public void ShowInformation(string title, string message) => ShowNotice(title, message, isError: false);

    public bool Confirm(string title, string message)
        => ShowConfirmDialog(title, message, showDontAskAgain: false, dontAskAgainLabel: string.Empty).Confirmed;

    public (bool Confirmed, bool DontAskAgain) ConfirmWithOptOut(string title, string message, string dontAskAgainLabel = "Don't ask me again")
        => ShowConfirmDialog(title, message, showDontAskAgain: true, dontAskAgainLabel);

    /// <summary>
    /// A themed dialog rather than <see cref="MessageBox"/> for confirmations -
    /// a plain Windows message box does not pick up the app's own dark/light
    /// theme and stands out against the rest of the UI.
    /// </summary>
    private (bool Confirmed, bool DontAskAgain) ShowConfirmDialog(string title, string message, bool showDontAskAgain, string dontAskAgainLabel)
    {
        var viewModel = new ConfirmDialogViewModel(title, message, showDontAskAgain, dontAskAgainLabel);
        var window = new ConfirmDialog(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        var confirmed = window.ShowDialog() == true;
        return (confirmed, viewModel.DontAskAgain);
    }

    public bool ShowAddDeviceDialog()
    {
        var viewModel = _services.GetRequiredService<AddDeviceViewModel>();
        var window = new AddDeviceWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        var added = window.ShowDialog() == true;

        // "Add several..." closes this dialog and opens the bulk one in its place.
        return window.SwitchToBulkAdd ? ShowBulkAddDevicesDialog() : added;
    }

    public bool ShowBulkAddDevicesDialog()
    {
        var viewModel = _services.GetRequiredService<BulkAddDevicesViewModel>();
        var window = new BulkAddDevicesWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        window.ShowDialog();
        return viewModel.AnyAdded;
    }

    public bool ShowAddDeviceGroupDialog()
    {
        var viewModel = _services.GetRequiredService<DeviceGroupEditorViewModel>();
        return ShowDeviceGroupEditorDialog(viewModel);
    }

    public bool ShowEditDeviceGroupDialog(DeviceGroup group)
    {
        var viewModel = _services.GetRequiredService<DeviceGroupEditorViewModel>();
        viewModel.Initialize(group);
        return ShowDeviceGroupEditorDialog(viewModel);
    }

    private bool ShowDeviceGroupEditorDialog(DeviceGroupEditorViewModel viewModel)
    {
        var window = new DeviceGroupEditorWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        return window.ShowDialog() == true;
    }

    public bool ShowAddDevicesToGroupDialog(IReadOnlyList<int> deviceIds)
    {
        var viewModel = _services.GetRequiredService<AddDevicesToGroupViewModel>();
        viewModel.Initialize(deviceIds);

        var window = new AddDevicesToGroupWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        return window.ShowDialog() == true;
    }

    public bool ShowAddLocationDialog()
    {
        var viewModel = _services.GetRequiredService<LocationEditorViewModel>();
        return ShowLocationEditorDialog(viewModel);
    }

    public bool ShowEditLocationDialog(Location location)
    {
        var viewModel = _services.GetRequiredService<LocationEditorViewModel>();
        viewModel.Initialize(location);
        return ShowLocationEditorDialog(viewModel);
    }

    private bool ShowLocationEditorDialog(LocationEditorViewModel viewModel)
    {
        var window = new LocationEditorWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        return window.ShowDialog() == true;
    }

    public bool ShowAddRuleDialog()
    {
        var viewModel = _services.GetRequiredService<RuleEditorViewModel>();
        return ShowRuleEditorDialog(viewModel);
    }

    public bool ShowEditRuleDialog(AlertRule rule)
    {
        var viewModel = _services.GetRequiredService<RuleEditorViewModel>();
        viewModel.Initialize(rule);
        return ShowRuleEditorDialog(viewModel);
    }

    private bool ShowRuleEditorDialog(RuleEditorViewModel viewModel)
    {
        var window = new RuleEditorWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        return window.ShowDialog() == true;
    }

    public string? ShowScheduleMaintenanceDialog(int deviceId, string deviceName)
    {
        var viewModel = _services.GetRequiredService<MaintenanceScheduleViewModel>();
        viewModel.Initialize(deviceId, deviceName);

        var window = new MaintenanceScheduleWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        return window.ShowDialog() == true ? viewModel.ConfirmationMessage : null;
    }

    public bool ShowAddAlertTemplateDialog()
    {
        var viewModel = _services.GetRequiredService<AlertTemplateEditorViewModel>();
        return ShowAlertTemplateEditorDialog(viewModel);
    }

    public bool ShowEditAlertTemplateDialog(AlertTemplate template)
    {
        var viewModel = _services.GetRequiredService<AlertTemplateEditorViewModel>();
        viewModel.Initialize(template);
        return ShowAlertTemplateEditorDialog(viewModel);
    }

    private bool ShowAlertTemplateEditorDialog(AlertTemplateEditorViewModel viewModel)
    {
        var window = new AlertTemplateEditorWindow(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        return window.ShowDialog() == true;
    }

    /// <summary>
    /// The same themed dialog as <see cref="ShowConfirmDialog"/>, in OK-only
    /// mode, for a plain notice - see <see cref="ShowError"/>/<see cref="ShowInformation"/>.
    /// Used in place of <see cref="MessageBox"/> so it doesn't stand out
    /// against the app's own dark/light theme the way the OS's plain system
    /// dialog does.
    /// </summary>
    private void ShowNotice(string title, string message, bool isError)
    {
        var viewModel = new ConfirmDialogViewModel(
            title, message,
            showDontAskAgain: false, dontAskAgainLabel: string.Empty,
            showCancel: false, confirmLabel: "OK", isError: isError);
        var window = new ConfirmDialog(viewModel);
        RememberPlacement(window);

        if (_mainWindow is { IsVisible: true })
        {
            OwnByMain(window);
        }

        window.ShowDialog();
    }

    public void Exit()
    {
        if (Application.Current is App app)
        {
            app.ShutdownApplication();
        }
        else
        {
            Application.Current?.Shutdown();
        }
    }
}

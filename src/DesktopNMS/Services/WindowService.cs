using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
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

    public void ShowDeviceDetail(int deviceId)
    {
        if (_openDeviceWindows.TryGetValue(deviceId, out var existing))
        {
            existing.Activate();
            return;
        }

        var settings = _services.GetRequiredService<ISettingsStore>();
        var viewModel = new DeviceDetailViewModel(
            deviceId,
            _services.GetRequiredService<DeviceMonitor>(),
            _services.GetRequiredService<SensorMonitor>(),
            _services.GetRequiredService<AlertMonitor>(),
            _services.GetRequiredService<IDeviceCache>(),
            _services.GetRequiredService<ILibreNmsClient>(),
            _services.GetRequiredService<IAlertRuleCache>(),
            _services.GetRequiredService<ISessionService>(),
            settings,
            this,
            _services.GetRequiredService<IUnimusApi>(),
            _services.GetRequiredService<IUnimusDeviceResolver>(),
            _services.GetRequiredService<ILogger<DeviceDetailViewModel>>());

        var window = new DeviceView(viewModel, settings);

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
        }

        window.Closed += (_, _) =>
        {
            _openDeviceWindows.Remove(deviceId);
            viewModel.Dispose();
        };

        _openDeviceWindows[deviceId] = window;
        window.Show();
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

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
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

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
        }

        window.ShowDialog();
    }

    public void ShowAlertFiltersDialog()
    {
        // Same live-object reasoning as ShowDeviceFiltersDialog: MainViewModel
        // is a singleton, so its GroupFilter here is the one filtering the grid.
        var window = new AlertFiltersWindow(_services.GetRequiredService<MainViewModel>());

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
        }

        window.ShowDialog();
    }

    public bool ShowSignInDialog()
    {
        var viewModel = _services.GetRequiredService<ConnectionViewModel>();
        var window = new ConnectionWindow(viewModel);

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
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

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
        }

        var confirmed = window.ShowDialog() == true;
        return (confirmed, viewModel.DontAskAgain);
    }

    public bool ShowAddDeviceDialog()
    {
        var viewModel = _services.GetRequiredService<AddDeviceViewModel>();
        var window = new AddDeviceWindow(viewModel);

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
        }

        return window.ShowDialog() == true;
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

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
        }

        return window.ShowDialog() == true;
    }

    public bool ShowAddDevicesToGroupDialog(IReadOnlyList<int> deviceIds)
    {
        var viewModel = _services.GetRequiredService<AddDevicesToGroupViewModel>();
        viewModel.Initialize(deviceIds);

        var window = new AddDevicesToGroupWindow(viewModel);

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
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

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
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

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
        }

        return window.ShowDialog() == true;
    }

    public string? ShowScheduleMaintenanceDialog(int deviceId, string deviceName)
    {
        var viewModel = _services.GetRequiredService<MaintenanceScheduleViewModel>();
        viewModel.Initialize(deviceId, deviceName);

        var window = new MaintenanceScheduleWindow(viewModel);

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
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

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
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

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
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

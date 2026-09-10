using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
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

    public void ShowAlertsForDevice(string deviceSearchTerm)
    {
        _services.GetRequiredService<MainViewModel>().ShowAlertsForDevice(deviceSearchTerm);
        ShowMain();
    }

    public void ShowDeviceDetail(int deviceId)
    {
        if (_openDeviceWindows.TryGetValue(deviceId, out var existing))
        {
            existing.Activate();
            return;
        }

        var viewModel = new DeviceDetailViewModel(
            deviceId,
            _services.GetRequiredService<DeviceMonitor>(),
            _services.GetRequiredService<SensorMonitor>(),
            _services.GetRequiredService<IDeviceCache>(),
            _services.GetRequiredService<ILibreNmsClient>(),
            _services.GetRequiredService<ISessionService>(),
            _services.GetRequiredService<ISettingsStore>(),
            this,
            _services.GetRequiredService<ILogger<DeviceDetailViewModel>>());

        var window = new DeviceView(viewModel);

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

    public void ShowError(string title, string message)
        => ShowMessage(title, message, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInformation(string title, string message)
        => ShowMessage(title, message, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string title, string message)
        => ShowMessage(title, message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private MessageBoxResult ShowMessage(string title, string message, MessageBoxButton button, MessageBoxImage image)
    {
        // A message box with no owner is the right thing when the window is
        // hidden in the tray; passing a hidden window would make it invisible.
        return _mainWindow is { IsVisible: true } owner
            ? MessageBox.Show(owner, message, title, button, image)
            : MessageBox.Show(message, title, button, image);
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

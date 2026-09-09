using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DesktopNMS.Services;

/// <summary>Adds or removes the per-user "start at sign-in" registration.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

/// <summary>
/// Writes to HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Per-user, so it
/// needs no elevation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunKeyStartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DashyNMS";

    private readonly ILogger<RunKeyStartupRegistration> _logger;

    public RunKeyStartupRegistration(ILogger<RunKeyStartupRegistration> logger)
    {
        _logger = logger;
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the Run key");
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var path = GetExecutablePath();
                if (path is null)
                {
                    _logger.LogWarning("Could not determine the executable path; start-with-Windows not registered");
                    return;
                }

                // --minimised so an auto-start lands in the tray rather than
                // popping a window in the user's face at sign-in.
                key.SetValue(ValueName, $"\"{path}\" --minimised", RegistryValueKind.String);
                _logger.LogInformation("Registered DashyNMS to start at sign-in");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.LogInformation("Removed the start-at-sign-in registration");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not update the Run key");
        }
    }

    private static string? GetExecutablePath()
    {
        // Environment.ProcessPath is the real .exe for both framework-dependent
        // and single-file published builds; the entry assembly location is empty
        // for single-file, so do not use it.
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
        {
            return path;
        }

        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName;
    }
}

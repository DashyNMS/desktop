using System;
using DesktopNMS.Core.Configuration;
using Microsoft.Win32;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// The palette actually in use. Chosen once at startup (see
/// <c>App.ApplyTheme</c>) - WPF resolves the styles built from it when they
/// are first parsed, so a change needs a restart - and read by anything that
/// has to match it, like the map tiles, rather than the setting itself, which
/// may be "Match Windows" (#81).
/// </summary>
public static class ThemeState
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Dark or Light - never <see cref="AppTheme.System"/>.</summary>
    public static AppTheme Effective { get; set; } = AppTheme.Dark;

    public static bool IsDark => Effective == AppTheme.Dark;

    /// <summary>
    /// Windows' own "choose your app mode" setting - the AppsUseLightTheme
    /// value, 1 for light and 0 for dark. Light (Windows' own default) when
    /// it can't be read.
    /// </summary>
    public static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }
}

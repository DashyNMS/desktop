using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using DesktopNMS.Core.SignIn;
using DesktopNMS.Views;

namespace DesktopNMS.Services;

/// <summary>Use an isolated native browser session for LibreNMS's own sign-in page.</summary>
public sealed class AvaloniaWebSignIn : IWebSignIn
{
    public string DeviceName => Environment.MachineName;

    public async Task<WebSignInResult> SignInAsync(WebSignInRequest request)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.Windows.OfType<ConnectionWindow>().FirstOrDefault(window => window.IsVisible) is not { } owner)
            return new(WebSignInOutcome.Failed);
        try { return await new LibreNmsSignInWindow(request).ShowDialog<WebSignInResult>(owner) ?? WebSignInResult.Cancelled; }
        catch (Exception) { return new(WebSignInOutcome.Failed); }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DesktopNMS.Core.SignIn;
using DesktopNMS.Views;
using Microsoft.Web.WebView2.Core;

namespace DesktopNMS.Services;

/// <summary>
/// Desktop's <see cref="IWebSignIn"/>: LibreNMS's website in WebView2 (see
/// <see cref="LibreNmsSignInWindow"/>), the token named after this PC.
/// </summary>
public sealed class WebView2SignIn : IWebSignIn
{
    /// <summary>
    /// Whether the WebView2 runtime is installed. It comes with Windows 11, and
    /// with Edge on most of Windows 10 - but not every machine. Without it the
    /// connection window offers only the API token, as the phone does without
    /// a web view.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string DeviceName => Environment.MachineName;

    public async Task<WebSignInResult> SignInAsync(WebSignInRequest request)
    {
        var window = new LibreNmsSignInWindow(request)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };

        window.ShowDialog();
        return await window.Result.ConfigureAwait(true);
    }
}

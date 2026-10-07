using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.SignIn;
using Microsoft.Web.WebView2.Core;

namespace DesktopNMS.Views;

/// <summary>
/// "Sign in with LibreNMS" (mobile#162) on desktop: the server's website in
/// WebView2, driven by Core's <see cref="WebTokenSignIn"/> - the same flow
/// as the phone's. Shown modally by <see cref="Services.WebView2SignIn"/>.
/// </summary>
/// <remarks>
/// Each attempt gets its own throwaway WebView2 profile in %TEMP%, deleted
/// afterwards, so LibreNMS isn't left logged in and no cookies outlive it. A
/// self-signed server's certificate is accepted only when its fingerprint is
/// one the user already trusted (#189), and only for the server's own host.
/// </remarks>
public partial class LibreNmsSignInWindow : Window
{
    private readonly WebSignInRequest _request;
    private readonly string _profileFolder = Path.Combine(Path.GetTempPath(), "DashyNMS-SignIn-" + Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource<WebSignInResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _handling;

    public LibreNmsSignInWindow(WebSignInRequest request)
    {
        _request = request;
        InitializeComponent();
        HostText.Text = request.Flow.TokensPage.Host;

        Loaded += async (_, _) => await StartAsync();
        Closed += OnClosed;
    }

    public Task<WebSignInResult> Result => _result.Task;

    private async Task StartAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileFolder);
            await Web.EnsureCoreWebView2Async(environment);

            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.ServerCertificateErrorDetected += OnServerCertificateError;
            core.NavigationCompleted += OnNavigationCompleted;

            // A sign-on page that opens a window: keep it in this one.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            };

            core.Navigate(_request.Flow.TokensPage.AbsoluteUri);
        }
        catch (Exception)
        {
            Finish(new WebSignInResult(WebSignInOutcome.Failed));
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_result.Task.IsCompleted || _handling)
        {
            return;
        }

        if (!e.IsSuccess)
        {
            // Cancelled: a redirect replaced it, and the next page reports in.
            if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                Finish(new WebSignInResult(WebSignInOutcome.Failed));
            }

            return;
        }

        _handling = true;
        try
        {
            Uri.TryCreate(Web.CoreWebView2.Source, UriKind.Absolute, out var location);

            // Scripts run on the server's own pages only, never a sign-on provider's.
            string? probe = null;
            if (_request.Flow.IsOnServer(location))
            {
                probe = await RunScriptAsync(WebTokenSignIn.ProbeScript);
            }

            var step = _request.Flow.Next(location, probe);
            switch (step.Action)
            {
                case WebSignInAction.Wait:
                    ShowPage();
                    break;

                case WebSignInAction.GoToTokensPage:
                    Working("Opening API Tokens…");
                    Web.CoreWebView2.Navigate(_request.Flow.TokensPage.AbsoluteUri);
                    break;

                case WebSignInAction.Submit:
                    Working("Creating a token for DashyNMS…");
                    await RunScriptAsync(_request.Flow.SubmitScript);
                    break;

                case WebSignInAction.Done:
                    Finish(new WebSignInResult(WebSignInOutcome.Token, step.Token));
                    break;

                case WebSignInAction.NotAllowed:
                    Finish(new WebSignInResult(WebSignInOutcome.NotAllowed));
                    break;

                case WebSignInAction.Failed:
                    Finish(new WebSignInResult(WebSignInOutcome.Failed));
                    break;
            }
        }
        finally
        {
            _handling = false;
        }
    }

    /// <summary>
    /// The web view can't ask about a certificate, so the connection window
    /// asked first (<see cref="ICertificateProbe"/>): accept exactly what was
    /// trusted there, for the server's host, and nothing else.
    /// </summary>
    private void OnServerCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var trusted = _request.AllowUntrustedCertificate
            && Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri)
            && _request.Flow.IsServerHost(uri.Host)
            && CertificateTrust.IsTrusted(_request.TrustedCertificates, Encoded(e.ServerCertificate));

        e.Action = trusted ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow : CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private static byte[]? Encoded(CoreWebView2Certificate? certificate)
    {
        if (certificate is null)
        {
            return null;
        }

        using var x509 = certificate.ToX509Certificate2();
        return x509.RawData;
    }

    private async Task<string?> RunScriptAsync(string script)
    {
        try
        {
            return await Web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
            // A page that went away mid-script: its replacement reports in.
            return null;
        }
    }

    private void ShowPage()
    {
        Busy.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
    }

    private void Working(string text)
    {
        Busy.Text = text;
        Busy.Visibility = Visibility.Visible;
        Web.Visibility = Visibility.Hidden;
    }

    private void Finish(WebSignInResult result)
    {
        if (_result.TrySetResult(result))
        {
            Working(result.Outcome == WebSignInOutcome.Token ? "Signing in…" : "Closing…");
            Dispatcher.BeginInvoke(Close);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(WebSignInResult.Cancelled);

    private void OnClosed(object? sender, EventArgs e)
    {
        _result.TrySetResult(WebSignInResult.Cancelled);
        Web.Dispose();
        _ = DeleteProfileAsync(_profileFolder);
    }

    /// <summary>The throwaway profile, once WebView2's processes have let go of it - best effort.</summary>
    private static async Task DeleteProfileAsync(string folder)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

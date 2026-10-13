using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.SignIn;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger _logger;

    /// <summary>
    /// The page's title and the start of its text, for the log when a page on
    /// the server is neither the form nor the token - an error page, say.
    /// </summary>
    private const string DescribeScript =
        "(function(){var b=document.body?document.body.innerText:'';" +
        "return (document.title||'(no title)')+' | '+b.replace(/\\s+/g,' ').trim().substring(0,200);})()";

    public LibreNmsSignInWindow(WebSignInRequest request, ILogger logger)
    {
        _request = request;
        _logger = logger;
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
            _logger.LogInformation(
                "Web sign-in: starting at {Page}, WebView2 {Version}",
                _request.Flow.TokensPage.GetLeftPart(UriPartial.Path),
                CoreWebView2Environment.GetAvailableBrowserVersionString());

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

            core.NavigationStarting += (_, e) =>
                _logger.LogInformation("Web sign-in: loading {Page} (redirect: {Redirect})", Describe(e.Uri), e.IsRedirected);

            core.Navigate(_request.Flow.TokensPage.AbsoluteUri);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web sign-in: the web view couldn't start");
            Finish(new WebSignInResult(WebSignInOutcome.Failed));
        }
    }

    /// <summary>A page's address for the log: scheme, host, port and path, never the query (it can carry codes).</summary>
    private static string Describe(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "(none)";

    /// <summary>The probe's answer for the log, with a token's value left out.</summary>
    private static string Redact(string answer) =>
        answer.StartsWith("token:", StringComparison.Ordinal) ? $"token ({answer.Length - "token:".Length} characters)" : answer;

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _logger.LogInformation(
            "Web sign-in: loaded {Page} - success {Success}, HTTP {Status}, error {Error}",
            Describe(Web.CoreWebView2?.Source), e.IsSuccess, e.HttpStatusCode, e.WebErrorStatus);

        if (_result.Task.IsCompleted || _handling)
        {
            _logger.LogInformation("Web sign-in: page ignored ({Reason})", _handling ? "still handling the previous page" : "already finished");
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

            var answer = probe is null ? "(not run - not the server)" : Redact(WebTokenSignIn.Unwrap(probe));
            _logger.LogInformation("Web sign-in: page is {Answer}, next step {Step}", answer, step.Action);

            // Neither the form nor the token, on the server: what is it? An
            // error page shows its status in the title or first line.
            if (probe is not null && step.Action is WebSignInAction.NotAllowed or WebSignInAction.Failed or WebSignInAction.Wait)
            {
                _logger.LogInformation("Web sign-in: page says {Text}", WebTokenSignIn.Unwrap(await RunScriptAsync(DescribeScript)));
            }

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
                    var submitted = await RunScriptAsync(_request.Flow.SubmitScript);
                    _logger.LogInformation("Web sign-in: submit script answered {Answer}", WebTokenSignIn.Unwrap(submitted));
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
        _logger.LogInformation("Web sign-in: certificate error {Status} on {Page}, {Action}", e.ErrorStatus, Describe(e.RequestUri), trusted ? "trusted" : "refused");
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
            _logger.LogInformation("Web sign-in: finished - {Outcome}", result.Outcome);
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

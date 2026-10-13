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
    /// <summary>
    /// How long the tokens page is left before the form is submitted, for
    /// LibreNMS's own scripts to finish - its timezone check can reload the
    /// page straight after signing in (#293).
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;

    // One page is handled at a time; one that finishes loading meanwhile is
    // picked up straight after, not dropped.
    private bool _handling;
    private bool _recheck;

    // The newest navigation started, and the newest that finished loading:
    // a page an older navigation left is never acted on.
    private ulong _latestNavigation;
    private ulong _lastLoaded;

    // Whether the token form's POST went out after the last submit, and
    // what LibreNMS answered it.
    private bool _awaitingPost;
    private bool _postSent;
    private int _postStatus;

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
            {
                _latestNavigation = e.NavigationId;
                _logger.LogInformation(
                    "Web sign-in: loading {Page} (navigation {Id}, redirect: {Redirect}, kind: {Kind})",
                    Describe(e.Uri), e.NavigationId, e.IsRedirected, e.NavigationKind);
            };

            core.WebResourceResponseReceived += OnResponseReceived;

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

    /// <summary>
    /// The server's pages' responses (not its scripts or styles), for the
    /// log - and to see whether the token form's POST went out, and how
    /// LibreNMS answered it.
    /// </summary>
    private void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            || !_request.Flow.IsOnServer(uri)
            || Path.GetExtension(uri.AbsolutePath).Length != 0)
        {
            return;
        }

        var location = e.Response.Headers.Contains("Location") ? e.Response.Headers.GetHeader("Location") : null;
        _logger.LogInformation(
            "Web sign-in: {Method} {Page} answered HTTP {Status}{Location}",
            e.Request.Method, Describe(uri.AbsoluteUri), e.Response.StatusCode,
            location is null ? string.Empty : " -> " + Describe(new Uri(uri, location).AbsoluteUri));

        if (string.Equals(e.Request.Method, "POST", StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath.TrimEnd('/'), _request.Flow.TokensPage.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            _postSent = true;
            _postStatus = e.Response.StatusCode;
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _logger.LogInformation(
            "Web sign-in: loaded {Page} (navigation {Id}) - success {Success}, HTTP {Status}, error {Error}",
            Describe(Web.CoreWebView2?.Source), e.NavigationId, e.IsSuccess, e.HttpStatusCode, e.WebErrorStatus);

        if (_result.Task.IsCompleted)
        {
            return;
        }

        // Another page is already on its way - it reports in when it's loaded.
        if (e.NavigationId != _latestNavigation)
        {
            _logger.LogInformation("Web sign-in: navigation {Id} was overtaken by {Latest}", e.NavigationId, _latestNavigation);
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

        _lastLoaded = e.NavigationId;
        if (_handling)
        {
            _recheck = true;
            return;
        }

        _handling = true;
        try
        {
            do
            {
                _recheck = false;
                await HandlePageAsync(_lastLoaded);
            }
            while (_recheck && !_result.Task.IsCompleted);
        }
        finally
        {
            _handling = false;
        }
    }

    private async Task HandlePageAsync(ulong navigation)
    {
        Uri.TryCreate(Web.CoreWebView2.Source, UriKind.Absolute, out var location);

        // Scripts run on the server's own pages only, never a sign-on provider's.
        string? probe = null;
        if (_request.Flow.IsOnServer(location))
        {
            probe = await RunScriptAsync(WebTokenSignIn.ProbeScript);
        }

        if (navigation != _latestNavigation)
        {
            _logger.LogInformation("Web sign-in: the page changed while it was being checked");
            return;
        }

        // A submit with no POST before this page: a reload landed on top of
        // it (#293), so the form gets submitted again.
        if (_awaitingPost)
        {
            _awaitingPost = false;
            if (!_postSent)
            {
                _logger.LogInformation("Web sign-in: the form's submit never went out");
                _request.Flow.SubmitNotSent();
            }
        }

        var step = _request.Flow.Next(location, probe);

        // Neither the form nor the token after LibreNMS turned the POST down
        // (an expired session, a server error): that's a failure, not a
        // missing permission.
        if (step.Action == WebSignInAction.NotAllowed && _postSent && _postStatus >= 400)
        {
            step = new WebSignInStep(WebSignInAction.Failed);
        }

        var answer = probe is null ? "(not run - not the server)" : Redact(WebTokenSignIn.Unwrap(probe));
        _logger.LogInformation("Web sign-in: page is {Answer}, next step {Step}", answer, step.Action);

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
                await SubmitAsync(navigation);
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

    /// <summary>
    /// Submits the token form once the page has settled. If LibreNMS reloads
    /// it first, or the submit doesn't run, it's left for the next load to
    /// submit again (#293).
    /// </summary>
    private async Task SubmitAsync(ulong navigation)
    {
        Working("Creating a token for DashyNMS…");
        await Task.Delay(SettleTime);

        if (_result.Task.IsCompleted)
        {
            return;
        }

        if (navigation != _latestNavigation)
        {
            _logger.LogInformation("Web sign-in: the page reloaded before the form was submitted - submitting on the new page");
            _request.Flow.SubmitSkipped();
            return;
        }

        _postSent = false;
        _postStatus = 0;
        _awaitingPost = true;

        var submitted = WebTokenSignIn.Unwrap(await RunScriptAsync(_request.Flow.SubmitScript));
        _logger.LogInformation("Web sign-in: submit script answered {Answer}", submitted);

        if (submitted != "submitted")
        {
            _awaitingPost = false;
            _request.Flow.SubmitNotSent();

            // Nothing else is loading, so no page will come to try again on.
            if (navigation == _latestNavigation)
            {
                Finish(new WebSignInResult(WebSignInOutcome.Failed));
            }
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

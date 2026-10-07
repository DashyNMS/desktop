using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.SignIn;
using DesktopNMS.Demo;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Sign-in dialog: the server address, then "Sign in with LibreNMS" - sign
/// in on the server's own website and let DashyNMS create its API token
/// (mobile#162, the flow in Core's <see cref="WebTokenSignIn"/>) - or, as the
/// way back when that can't work, an API token pasted in.
/// </summary>
public sealed class ConnectionViewModel : ObservableObject
{
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly DemoMode _demo;
    private readonly IWebSignIn? _webSignIn;
    private readonly ICertificateProbe _probe;
    private bool _usesToken;

    private string _serverUrl = string.Empty;
    private string _apiToken = string.Empty;
    private bool _allowUntrustedCertificate;
    private bool _rememberToken = true;
    private bool _isBusy;
    private string? _errorMessage;
    private string? _successMessage;

    public ConnectionViewModel(
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        DemoMode demo,
        ITokenProtector tokens,
        ICertificateProbe probe,
        IWebSignIn? webSignIn = null)
    {
        _session = session;
        _settings = settings;
        _windows = windows;
        _demo = demo;
        _probe = probe;
        _webSignIn = webSignIn;

        var current = settings.Current;
        _serverUrl = current.ServerUrl ?? string.Empty;
        _backupAddress = current.BackupServerAddress ?? string.Empty;
        _allowUntrustedCertificate = current.AllowUntrustedCertificate;
        _rememberToken = current.RememberToken;

        // A token already saved here: signing in again needs nothing new, so the token form it is.
        _usesToken = webSignIn is null || tokens.HasStoredToken;

        ConnectCommand = new AsyncRelayCommand(() => ConnectAsync(), () => !IsBusy);
        TryDemoCommand = new RelayCommand(TryDemo, () => !IsBusy);
        SignInWithLibreNmsCommand = new AsyncRelayCommand(SignInWithLibreNmsAsync, () => !IsBusy);
        UseTokenCommand = new RelayCommand(() => SetUsesToken(true));
        UseLibreNmsCommand = new RelayCommand(() => SetUsesToken(false));
    }

    /// <summary>"Sign in with LibreNMS": the server's website, where DashyNMS then creates its own token.</summary>
    public AsyncRelayCommand SignInWithLibreNmsCommand { get; }

    public RelayCommand UseTokenCommand { get; }

    public RelayCommand UseLibreNmsCommand { get; }

    /// <summary>
    /// True once the user has chosen to paste an API token, or "Sign in with
    /// LibreNMS" couldn't work for this server - the form then asks for the
    /// token as it always did.
    /// </summary>
    public bool UsesToken
    {
        get => _usesToken;
        private set
        {
            if (SetProperty(ref _usesToken, value))
            {
                OnPropertyChanged(nameof(ShowsWebSignIn));
                OnPropertyChanged(nameof(ShowsTokenEntry));
                OnPropertyChanged(nameof(ShowsLibreNmsLink));
            }
        }
    }

    /// <summary>"Sign in with LibreNMS" is the way in, unless there's no WebView2 for it.</summary>
    public bool ShowsWebSignIn => _webSignIn is not null && !UsesToken;

    public bool ShowsTokenEntry => !ShowsWebSignIn;

    /// <summary>"Sign in with LibreNMS instead", under the token form - only where it can work.</summary>
    public bool ShowsLibreNmsLink => _webSignIn is not null && UsesToken;

    /// <summary>Raised with a token "Sign in with LibreNMS" made, when signing in with it then failed - the window puts it in the token box to try again.</summary>
    public event EventHandler<string>? TokenCreated;

    private void SetUsesToken(bool value)
    {
        ErrorMessage = null;
        UsesToken = value;
    }

    /// <summary>
    /// "Sign in with LibreNMS": checks the server answers and its certificate
    /// is trusted, shows its website for the user to sign in on, and signs in
    /// with the token created there - named after this PC and the day. When
    /// it can't work - plain http, an account without API access, LibreNMS
    /// before 26.4 - it says why and goes back to asking for a token.
    /// </summary>
    private async Task SignInWithLibreNmsAsync()
    {
        if (_webSignIn is null)
        {
            return;
        }

        ErrorMessage = null;
        SuccessMessage = null;
        if (!LibreNmsConnection.TryParseWebRoot(ServerUrl, out var webRoot, out var error))
        {
            ErrorMessage = error;
            return;
        }

        // The user types their password into this page, so never over plain http.
        if (!string.Equals(webRoot!.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            FallBack(WebSignInMessages.NeedsHttps);
            return;
        }

        IsBusy = true;
        try
        {
            var probe = await _probe.ProbeAsync(webRoot, AllowUntrustedCertificate, TrustedCertificates()).ConfigureAwait(true);
            if (probe.UntrustedCertificate is { } certificate)
            {
                if (_windows.ConfirmTrustCertificate("LibreNMS", certificate))
                {
                    _session.TrustCertificate(certificate);
                    probe = await _probe.ProbeAsync(webRoot, AllowUntrustedCertificate, TrustedCertificates()).ConfigureAwait(true);
                }
                else
                {
                    probe = new ProbeResult(false, ErrorMessage: WebSignInMessages.CertificateDeclined);
                }
            }

            if (!probe.Reached)
            {
                ErrorMessage = probe.ErrorMessage;
                return;
            }
        }
        finally
        {
            IsBusy = false;
        }

        var flow = new WebTokenSignIn(webRoot, WebTokenSignIn.NameFor(_webSignIn.DeviceName, DateTime.Now));
        var result = await _webSignIn.SignInAsync(new WebSignInRequest(flow, AllowUntrustedCertificate, TrustedCertificates())).ConfigureAwait(true);

        switch (result.Outcome)
        {
            case WebSignInOutcome.Token:
                ApiToken = result.Token!;
                if (!await ConnectAsync().ConfigureAwait(true))
                {
                    // The token's made: keep it in the (hidden) box to try again.
                    UsesToken = true;
                    TokenCreated?.Invoke(this, result.Token!);
                }

                break;

            case WebSignInOutcome.NotAllowed:
                FallBack(WebSignInMessages.NotAllowed);
                break;

            case WebSignInOutcome.Failed:
                FallBack(WebSignInMessages.Failed);
                break;
        }
    }

    private void FallBack(string message)
    {
        UsesToken = true;
        ErrorMessage = message;
    }

    private IReadOnlyCollection<string> TrustedCertificates() => _settings.Current.TrustedCertificates.ToArray();

    /// <summary>Raised with true once a session has been established. Cancelling is handled by the dialog itself.</summary>
    public event EventHandler<bool>? RequestClose;

    public AsyncRelayCommand ConnectCommand { get; }

    /// <summary>Restarts DashyNMS on an example network - see DemoMode. Not offered from inside the demo.</summary>
    public RelayCommand TryDemoCommand { get; }

    public bool CanTryDemo => !_demo.IsActive;

    private void TryDemo()
    {
        RequestClose?.Invoke(this, false);
        _demo.RequestStart();
    }

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    /// <summary>Optional: another address for the same server - an IP, or another name - used if the main one stops answering (see ServerFailover).</summary>
    public string BackupAddress
    {
        get => _backupAddress;
        set => SetProperty(ref _backupAddress, value);
    }

    private string _backupAddress;

    /// <summary>
    /// Bound from the PasswordBox code-behind rather than by two-way binding,
    /// because WPF's PasswordBox deliberately does not expose a bindable value.
    /// </summary>
    public string ApiToken
    {
        get => _apiToken;
        set => SetProperty(ref _apiToken, value);
    }

    public bool AllowUntrustedCertificate
    {
        get => _allowUntrustedCertificate;
        set => SetProperty(ref _allowUntrustedCertificate, value);
    }

    public bool RememberToken
    {
        get => _rememberToken;
        set => SetProperty(ref _rememberToken, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
                SignInWithLibreNmsCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public string? SuccessMessage
    {
        get => _successMessage;
        private set
        {
            if (SetProperty(ref _successMessage, value))
            {
                OnPropertyChanged(nameof(HasSuccess));
            }
        }
    }

    public bool HasSuccess => !string.IsNullOrEmpty(_successMessage);

    /// <summary>Signs in with <see cref="ApiToken"/> - pasted, or made by "Sign in with LibreNMS". True once connected.</summary>
    private async Task<bool> ConnectAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;
        IsBusy = true;

        try
        {
            // Long enough to try the backup address too, if the main one doesn't answer.
            var seconds = _settings.Current.TimeoutSeconds + 5;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(string.IsNullOrWhiteSpace(BackupAddress) ? seconds : seconds * 2));

            var result = await _session
                .SignInAsync(ServerUrl, ApiToken, AllowUntrustedCertificate, RememberToken, timeout.Token, BackupAddress)
                .ConfigureAwait(true);

            // A certificate the user hasn't accepted yet (or one that has
            // changed): show it, and only on their say-so trust it and try again (#189).
            if (result.UntrustedCertificate is { } certificate && _windows.ConfirmTrustCertificate("LibreNMS", certificate))
            {
                _session.TrustCertificate(certificate);
                using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                result = await _session
                    .SignInAsync(ServerUrl, ApiToken, AllowUntrustedCertificate, RememberToken, retryTimeout.Token, BackupAddress)
                    .ConfigureAwait(true);
            }

            if (result.Succeeded)
            {
                var version = result.SystemInfo?.LocalVersion;
                SuccessMessage = (version is null ? "Connected" : $"Connected to LibreNMS {version}") + (result.UsedBackupAddress ? " through the backup address." : ".");
                RequestClose?.Invoke(this, true);
                return true;
            }

            ErrorMessage = result.ErrorMessage;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "The connection attempt timed out.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        return false;
    }
}

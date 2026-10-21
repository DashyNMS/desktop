using System;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The Avalonia app's placeholder window (#153): sign in to LibreNMS, start the
/// alert monitor and show the open alert count - proof that the shared
/// sign-in, polling and view-model plumbing run on Avalonia. The ported views
/// replace it in #250.
/// </summary>
public sealed class ScaffoldViewModel : ObservableObject
{
    private readonly ISessionService _session;
    private readonly AlertMonitor _alerts;
    private readonly IUiDispatcher _dispatcher = UiThread.Dispatcher;
    private string _serverUrl = string.Empty;
    private string _token = string.Empty;
    private bool _allowUntrustedCertificate;
    private bool _isSigningIn;
    private string? _errorText;
    private string _alertSummary = "Waiting for the first poll…";
    private string _faultSummary = string.Empty;
    private string _pollText = string.Empty;

    public ScaffoldViewModel(ISessionService session, AlertMonitor alerts)
    {
        _session = session;
        _alerts = alerts;
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => !_isSigningIn && !string.IsNullOrWhiteSpace(_serverUrl) && !string.IsNullOrWhiteSpace(_token));
        _alerts.Polled += (_, result) => _dispatcher.Post(() => OnPolled(result));
    }

    public AsyncRelayCommand SignInCommand { get; }

    public string ServerUrl
    {
        get => _serverUrl;
        set { if (SetProperty(ref _serverUrl, value ?? string.Empty)) SignInCommand.RaiseCanExecuteChanged(); }
    }

    public string Token
    {
        get => _token;
        set { if (SetProperty(ref _token, value ?? string.Empty)) SignInCommand.RaiseCanExecuteChanged(); }
    }

    public bool AllowUntrustedCertificate
    {
        get => _allowUntrustedCertificate;
        set => SetProperty(ref _allowUntrustedCertificate, value);
    }

    public bool IsSignedIn => _session.IsConnected;

    public bool IsSignedOut => !IsSignedIn;

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    /// <summary>"LibreNMS 26.10.1 at nms.example.net".</summary>
    public string ServerText => _session.Connection is { } connection
        ? $"LibreNMS {_session.ServerInfo?.LocalVersion ?? "(version unknown)"} at {connection.WebRoot.Host}"
        : string.Empty;

    /// <summary>"12 open alerts" - one per device and rule, as the WPF app's badge counts them.</summary>
    public string AlertSummary
    {
        get => _alertSummary;
        private set => SetProperty(ref _alertSummary, value);
    }

    /// <summary>"40 faults · 3 acknowledged".</summary>
    public string FaultSummary
    {
        get => _faultSummary;
        private set => SetProperty(ref _faultSummary, value);
    }

    public string PollText
    {
        get => _pollText;
        private set => SetProperty(ref _pollText, value);
    }

    private async Task SignInAsync()
    {
        _isSigningIn = true;
        SignInCommand.RaiseCanExecuteChanged();
        ErrorText = null;
        try
        {
            // Never remembered: saving secrets on each OS is #154.
            var result = await _session.SignInAsync(_serverUrl, _token, _allowUntrustedCertificate, rememberToken: false).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = result.UntrustedCertificate is not null
                    ? "The server's certificate isn't trusted by this computer. Tick \"Allow an untrusted certificate\" if it's your own server's, and sign in again."
                    : result.ErrorMessage ?? "Couldn't sign in.";
                return;
            }

            Token = string.Empty;
            _alerts.Start();
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(IsSignedOut));
            OnPropertyChanged(nameof(ServerText));
        }
        finally
        {
            _isSigningIn = false;
            SignInCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnPolled(AlertPollResult result)
    {
        PollText = result.Succeeded
            ? $"Checked at {result.CompletedAt.ToLocalTime():HH:mm:ss}"
            : $"Last check failed: {result.ErrorMessage}";
        if (!result.Succeeded)
        {
            return;
        }

        var groups = AlertGrouping.ByDeviceAndRule(result.Alerts.Where(a => a.State != AlertState.Recovered));
        var faults = groups.Sum(g => g.FaultCount);
        var acknowledged = groups.Sum(g => g.AcknowledgedCount);
        AlertSummary = groups.Count == 1 ? "1 open alert" : $"{groups.Count} open alerts";
        FaultSummary = (faults == 1 ? "1 fault" : $"{faults} faults") + (acknowledged > 0 ? $" · {acknowledged} acknowledged" : string.Empty);
    }
}

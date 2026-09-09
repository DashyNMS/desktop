using System;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Sign-in dialog: server address plus an API token from LibreNMS
/// (Settings, API, API Access).
/// </summary>
public sealed class ConnectionViewModel : ObservableObject
{
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;

    private string _serverUrl = string.Empty;
    private string _apiToken = string.Empty;
    private bool _allowUntrustedCertificate;
    private bool _rememberToken = true;
    private bool _isBusy;
    private string? _errorMessage;
    private string? _successMessage;

    public ConnectionViewModel(ISessionService session, ISettingsStore settings)
    {
        _session = session;
        _settings = settings;

        var current = settings.Current;
        _serverUrl = current.ServerUrl ?? string.Empty;
        _allowUntrustedCertificate = current.AllowUntrustedCertificate;
        _rememberToken = current.RememberToken;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsBusy);
    }

    /// <summary>Raised with true once a session has been established. Cancelling is handled by the dialog itself.</summary>
    public event EventHandler<bool>? RequestClose;

    public AsyncRelayCommand ConnectCommand { get; }

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

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

    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;
        IsBusy = true;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.Current.TimeoutSeconds + 5));

            var result = await _session
                .SignInAsync(ServerUrl, ApiToken, AllowUntrustedCertificate, RememberToken, timeout.Token)
                .ConfigureAwait(true);

            if (result.Succeeded)
            {
                var version = result.SystemInfo?.LocalVersion;
                SuccessMessage = version is null ? "Connected." : $"Connected to LibreNMS {version}.";
                RequestClose?.Invoke(this, true);
                return;
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
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Security;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Owns "am I signed in, and to what". Everything else asks this rather than
/// poking the client directly.
/// </summary>
public interface ISessionService
{
    bool IsConnected { get; }

    LibreNmsConnection? Connection { get; }

    /// <summary>Version details of the connected instance, populated on sign-in.</summary>
    SystemInfo? ServerInfo { get; }

    event EventHandler? StateChanged;

    /// <summary>Validates the credentials, and on success makes them the active session.</summary>
    Task<ConnectionTestResult> SignInAsync(
        string serverUrl,
        string apiToken,
        bool allowUntrustedCertificate,
        bool rememberToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to sign in with the saved address and token. Returns false when
    /// there is nothing saved or the saved details no longer work.
    /// </summary>
    Task<ConnectionTestResult?> TryRestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends the session. Optionally deletes the stored token as well.</summary>
    void SignOut(bool forgetToken);
}

public sealed class SessionService : ISessionService
{
    private readonly ILibreNmsClient _client;
    private readonly ISettingsStore _settings;
    private readonly ITokenProtector _tokens;
    private readonly ILogger<SessionService> _logger;

    public SessionService(
        ILibreNmsClient client,
        ISettingsStore settings,
        ITokenProtector tokens,
        ILogger<SessionService> logger)
    {
        _client = client;
        _settings = settings;
        _tokens = tokens;
        _logger = logger;
    }

    public bool IsConnected => _client.IsConnected;

    public LibreNmsConnection? Connection => _client.Connection;

    public SystemInfo? ServerInfo { get; private set; }

    public event EventHandler? StateChanged;

    public async Task<ConnectionTestResult> SignInAsync(
        string serverUrl,
        string apiToken,
        bool allowUntrustedCertificate,
        bool rememberToken,
        CancellationToken cancellationToken = default)
    {
        if (!LibreNmsConnection.TryParseWebRoot(serverUrl, out var webRoot, out var urlError))
        {
            return ConnectionTestResult.Failure(urlError ?? "The server address is not valid.");
        }

        if (string.IsNullOrWhiteSpace(apiToken))
        {
            return ConnectionTestResult.Failure("Enter the API token from LibreNMS (Settings, API, API Access).");
        }

        var settings = _settings.Current;
        var connection = new LibreNmsConnection(
            webRoot!,
            apiToken.Trim(),
            allowUntrustedCertificate,
            settings.TimeoutSeconds);

        var result = await _client.TestAsync(connection, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return result;
        }

        _client.Connect(connection);
        ServerInfo = result.SystemInfo;

        settings.ServerUrl = webRoot!.ToString();
        settings.AllowUntrustedCertificate = allowUntrustedCertificate;
        settings.RememberToken = rememberToken;
        _settings.Save();

        if (rememberToken)
        {
            _tokens.Save(connection.ApiToken);
        }
        else
        {
            _tokens.Clear();
        }

        _logger.LogInformation("Signed in to {Host}", webRoot);
        StateChanged?.Invoke(this, EventArgs.Empty);

        return result;
    }

    public async Task<ConnectionTestResult?> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;

        if (string.IsNullOrWhiteSpace(settings.ServerUrl))
        {
            return null;
        }

        var token = _tokens.Load();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (!LibreNmsConnection.TryParseWebRoot(settings.ServerUrl, out var webRoot, out _))
        {
            return null;
        }

        var connection = new LibreNmsConnection(
            webRoot!,
            token!,
            settings.AllowUntrustedCertificate,
            settings.TimeoutSeconds);

        var result = await _client.TestAsync(connection, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            _client.Connect(connection);
            ServerInfo = result.SystemInfo;
            _logger.LogInformation("Restored the saved session for {Host}", webRoot);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _logger.LogWarning("Could not restore the saved session: {Error}", result.ErrorMessage);

            // A rejected token is never going to start working; drop it so the
            // user is asked for a new one rather than seeing the same failure
            // on every launch.
            if (result.IsAuthenticationFailure)
            {
                _tokens.Clear();
            }
        }

        return result;
    }

    public void SignOut(bool forgetToken)
    {
        _client.Disconnect();
        ServerInfo = null;

        if (forgetToken)
        {
            _tokens.Clear();
        }

        _logger.LogInformation("Signed out (token {Action})", forgetToken ? "deleted" : "kept");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

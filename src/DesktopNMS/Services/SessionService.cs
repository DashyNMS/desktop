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
        CancellationToken cancellationToken = default,
        string? backupAddress = null);

    /// <summary>
    /// Sets or clears the server's backup address (see <see cref="ServerFailover"/>)
    /// and applies it to the live connection straight away - back on the main
    /// address. False if it isn't a usable address.
    /// </summary>
    bool SetBackupAddress(string? backupAddress);

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
        CancellationToken cancellationToken = default,
        string? backupAddress = null)
    {
        if (!LibreNmsConnection.TryParseWebRoot(serverUrl, out var webRoot, out var urlError))
        {
            return ConnectionTestResult.Failure(urlError ?? "The server address is not valid.");
        }

        if (string.IsNullOrWhiteSpace(apiToken))
        {
            return ConnectionTestResult.Failure("Enter the API token from LibreNMS (Settings, API, API Access).");
        }

        if (!string.IsNullOrWhiteSpace(backupAddress) && !ServerFailover.IsValidAddress(backupAddress))
        {
            return ConnectionTestResult.Failure("The backup address should be an IP address or a hostname - no https://, path or port.");
        }

        var settings = _settings.Current;
        var connection = new LibreNmsConnection(
            webRoot!,
            apiToken.Trim(),
            allowUntrustedCertificate,
            settings.TimeoutSeconds,
            backupAddress);

        var result = await _client.TestAsync(connection, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return result;
        }

        _client.Connect(connection, result.UsedBackupAddress);
        ServerInfo = result.SystemInfo;

        settings.ServerUrl = webRoot!.ToString();
        settings.BackupServerAddress = connection.BackupAddress;
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

    public bool SetBackupAddress(string? backupAddress)
    {
        var address = string.IsNullOrWhiteSpace(backupAddress) ? null : backupAddress.Trim();
        if (address is not null && !ServerFailover.IsValidAddress(address))
        {
            return false;
        }

        var settings = _settings.Current;
        if (string.Equals(settings.BackupServerAddress, address, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        settings.BackupServerAddress = address;
        _settings.Save();

        // The live connection picks it up now, on the main address.
        if (_client.Connection is { } current)
        {
            _client.Connect(new LibreNmsConnection(current.WebRoot, current.ApiToken, current.AllowUntrustedCertificate, current.TimeoutSeconds, address));
        }

        _logger.LogInformation("Backup server address {Change}", address is null ? "cleared" : $"set to {address}");
        return true;
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
            settings.TimeoutSeconds,
            ServerFailover.IsValidAddress(settings.BackupServerAddress) ? settings.BackupServerAddress : null);

        var result = await _client.TestAsync(connection, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            _client.Connect(connection, result.UsedBackupAddress);
            ServerInfo = result.SystemInfo;
            _logger.LogInformation("Restored the saved session for {Host}{Backup}", webRoot, result.UsedBackupAddress ? " through its backup address" : string.Empty);
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

using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Entry point for the LibreNMS API.
/// </summary>
/// <remarks>
/// Grouped by resource so new areas of the LibreNMS API (ports, services,
/// inventory, health graphs, ...) can be added as new properties without
/// disturbing existing callers.
/// </remarks>
public interface ILibreNmsClient
{
    IAlertsApi Alerts { get; }

    IAlertRulesApi Rules { get; }

    IDevicesApi Devices { get; }

    ISensorsApi Sensors { get; }

    IPortsApi Ports { get; }

    ILinksApi Links { get; }

    ILogsApi Logs { get; }

    ISystemApi System { get; }

    /// <summary>The connection in use, or null when not signed in.</summary>
    LibreNmsConnection? Connection { get; }

    /// <summary>True once <see cref="Connect"/> has been called with a connection.</summary>
    bool IsConnected { get; }

    /// <summary>Points the client at an instance. Does not perform any network I/O.</summary>
    void Connect(LibreNmsConnection connection);

    /// <summary>Forgets the current connection.</summary>
    void Disconnect();

    /// <summary>
    /// Verifies the address and token by calling /api/v0/system.
    /// Never throws; inspect the result.
    /// </summary>
    Task<ConnectionTestResult> TestAsync(LibreNmsConnection connection, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a credential check.</summary>
public sealed class ConnectionTestResult
{
    private ConnectionTestResult(bool succeeded, SystemInfo? systemInfo, string? errorMessage, bool isAuthenticationFailure)
    {
        Succeeded = succeeded;
        SystemInfo = systemInfo;
        ErrorMessage = errorMessage;
        IsAuthenticationFailure = isAuthenticationFailure;
    }

    public bool Succeeded { get; }

    public SystemInfo? SystemInfo { get; }

    public string? ErrorMessage { get; }

    public bool IsAuthenticationFailure { get; }

    public static ConnectionTestResult Success(SystemInfo info) => new(true, info, null, false);

    public static ConnectionTestResult Failure(string message, bool isAuthenticationFailure = false)
        => new(false, null, message, isAuthenticationFailure);
}

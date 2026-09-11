using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopNMS.Core.Api;

/// <inheritdoc cref="ILibreNmsClient"/>
public sealed class LibreNmsClient : ILibreNmsClient, IDisposable
{
    private readonly LibreNmsTransport _transport;
    private readonly ILogger<LibreNmsClient> _logger;

    public LibreNmsClient(LibreNmsTransport transport, ILogger<LibreNmsClient> logger)
    {
        _transport = transport;
        _logger = logger;

        Alerts = new AlertsApi(transport);
        Rules = new AlertRulesApi(transport);
        Devices = new DevicesApi(transport);
        Sensors = new SensorsApi(transport);
        Ports = new PortsApi(transport);
        Links = new LinksApi(transport);
        Logs = new LogsApi(transport);
        System = new SystemApi(transport);
    }

    public IAlertsApi Alerts { get; }

    public IAlertRulesApi Rules { get; }

    public IDevicesApi Devices { get; }

    public ISensorsApi Sensors { get; }

    public IPortsApi Ports { get; }

    public ILinksApi Links { get; }

    public ILogsApi Logs { get; }

    public ISystemApi System { get; }

    public LibreNmsConnection? Connection => _transport.Connection;

    public bool IsConnected => _transport.Connection is not null;

    public void Connect(LibreNmsConnection connection) => _transport.Configure(connection);

    public void Disconnect() => _transport.Clear();

    public async Task<ConnectionTestResult> TestAsync(LibreNmsConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Test against a throwaway transport so a failed attempt cannot replace
        // a working connection that is already in use.
        using var probe = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance);
        probe.Configure(connection);

        var api = new SystemApi(probe);

        try
        {
            var info = await api.GetAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connection test succeeded against {Host} ({Version})", connection.WebRoot, info.LocalVersion);
            return ConnectionTestResult.Success(info);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ConnectionTestResult.Failure("The connection test was cancelled.");
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Connection test failed against {Host}", connection.WebRoot);
            return ConnectionTestResult.Failure(ex.ToUserMessage(), ex.IsAuthenticationFailure);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection test failed unexpectedly against {Host}", connection.WebRoot);
            return ConnectionTestResult.Failure(ex.Message);
        }
    }

    public void Dispose() => _transport.Dispose();
}

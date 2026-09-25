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
        AlertTemplates = new AlertTemplatesApi(transport);
        Devices = new DevicesApi(transport);
        Sensors = new SensorsApi(transport);
        Ports = new PortsApi(transport);
        Links = new LinksApi(transport);
        Health = new DeviceHealthApi(transport);
        Fdb = new FdbApi(transport);
        Arp = new ArpApi(transport);
        Vlans = new VlansApi(transport);
        DeviceGroups = new DeviceGroupsApi(transport);
        Locations = new LocationsApi(transport);
        Routing = new RoutingApi(transport);
        PollerGroups = new PollerGroupsApi(transport);
        Logs = new LogsApi(transport);
        System = new SystemApi(transport);
        Graphs = new GraphsApi(transport);
    }

    public IAlertsApi Alerts { get; }

    public IAlertRulesApi Rules { get; }

    public IAlertTemplatesApi AlertTemplates { get; }

    public IDevicesApi Devices { get; }

    public ISensorsApi Sensors { get; }

    public IPortsApi Ports { get; }

    public ILinksApi Links { get; }

    public IDeviceHealthApi Health { get; }

    public IFdbApi Fdb { get; }

    public IArpApi Arp { get; }

    public IVlansApi Vlans { get; }

    public IDeviceGroupsApi DeviceGroups { get; }

    public ILocationsApi Locations { get; }

    public IRoutingApi Routing { get; }

    public IPollerGroupsApi PollerGroups { get; }

    public ILogsApi Logs { get; }

    public ISystemApi System { get; }

    public IGraphsApi Graphs { get; }

    public LibreNmsConnection? Connection => _transport.Connection;

    public bool IsConnected => _transport.Connection is not null;

    public void Connect(LibreNmsConnection connection, bool startOnBackup = false) => _transport.Configure(connection, startOnBackup);

    public ServerFailover Failover => _transport.Failover;

    public void Disconnect() => _transport.Clear();

    public async Task<ConnectionTestResult> TestAsync(LibreNmsConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Test against a throwaway transport so a failed attempt cannot replace
        // a working connection that is already in use.
        using var probe = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance) { RetryTransientFailures = false };
        probe.Configure(connection);

        var api = new SystemApi(probe);

        try
        {
            var info = await api.GetAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connection test succeeded against {Host} ({Version})", connection.WebRoot, info.LocalVersion);
            return ConnectionTestResult.Success(info);
        }
        catch (LibreNmsApiException ex) when (connection.BackupAddress is not null && ServerFailover.IsUnreachable(ex))
        {
            // The main address didn't answer at all - the backup address
            // might (see ServerFailover), and if it does, connect there.
            _logger.LogWarning(ex, "{Host} didn't answer - trying the backup address {Backup}", connection.WebRoot.Host, connection.BackupAddress);
            return await TestBackupAsync(connection, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The connection test again, dialling the backup address - the main address didn't answer.</summary>
    private async Task<ConnectionTestResult> TestBackupAsync(LibreNmsConnection connection, CancellationToken cancellationToken)
    {
        using var probe = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance) { RetryTransientFailures = false };
        probe.Configure(connection, startOnBackup: true);

        try
        {
            var info = await new SystemApi(probe).GetAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connection test succeeded against {Host} through its backup address {Backup}", connection.WebRoot, connection.BackupAddress);
            return ConnectionTestResult.Success(info, usedBackupAddress: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ConnectionTestResult.Failure("The connection test was cancelled.");
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Connection test failed against the backup address {Backup} too", connection.BackupAddress);
            return ConnectionTestResult.Failure(
                $"Neither the server's address nor the backup address ({connection.BackupAddress}) answered: {ex.ToUserMessage()}",
                ex.IsAuthenticationFailure);
        }
    }

    public void Dispose() => _transport.Dispose();
}

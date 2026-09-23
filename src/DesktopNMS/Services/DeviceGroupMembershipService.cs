using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Fleet-wide device group membership (device id -> the names of every group
/// it belongs to), shared by every tab that filters by group - the Devices
/// tab's Group facet and the Alerts tab's (issue #162). One fetch and one
/// refresh timer between them, rather than each tab paying for its own:
/// LibreNMS has no bulk endpoint for this, so building it costs one API call
/// per group (see <see cref="IDeviceGroupsApi.GetMembershipByDeviceAsync"/>).
/// </summary>
public interface IDeviceGroupMembershipService
{
    /// <summary>Raised on the UI thread whenever <see cref="GroupsFor"/> may have changed.</summary>
    event EventHandler? Changed;

    /// <summary>The names of every group this device belongs to - empty when none, or before the first fetch completes.</summary>
    IReadOnlyList<string> GroupsFor(int deviceId);

    /// <summary>Every group with at least one member, sorted by name - e.g. the network map's scope picker.</summary>
    IReadOnlyList<string> GroupNames { get; }

    /// <summary>Fetches now if nothing has yet, and starts the periodic background refresh. Safe to call repeatedly.</summary>
    void EnsureStarted();

    /// <summary>Re-fetches immediately (coalesced with one already in flight) - after a group is created/edited/deleted, or an explicit refresh.</summary>
    Task RefreshAsync();

    /// <summary>Forgets everything and stops refreshing - on sign-out, since the next server's groups and device ids are unrelated.</summary>
    void Clear();
}

public sealed class DeviceGroupMembershipService : IDeviceGroupMembershipService
{
    /// <summary>
    /// Refreshes this many times less often than the device poll - group
    /// membership changes far less often than device state, so a multiple of
    /// <see cref="AppSettings.PollIntervalSeconds"/> scales with whatever
    /// cadence the user has already chosen rather than a flat constant.
    /// </summary>
    private const int RefreshMultiplier = 10;

    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly ILogger<DeviceGroupMembershipService> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private IReadOnlyDictionary<int, IReadOnlyList<string>> _membership = new Dictionary<int, IReadOnlyList<string>>();
    private Task? _inFlight;
    private bool _started;

    /// <summary>Bumped by <see cref="Clear"/> so a fetch that was already in flight at sign-out can't repopulate the old server's data afterwards.</summary>
    private int _generation;

    public DeviceGroupMembershipService(
        ILibreNmsClient client,
        ISessionService session,
        ISettingsStore settings,
        ILogger<DeviceGroupMembershipService> logger)
    {
        _client = client;
        _session = session;
        _settings = settings;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = RefreshInterval() };
        _timer.Tick += (_, _) => _ = RefreshAsync();

        // Changing Interval on a running DispatcherTimer takes effect
        // immediately - picks up a mid-session poll-interval change.
        _settings.Changed += (_, _) => _timer.Interval = RefreshInterval();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<string> GroupsFor(int deviceId) =>
        _membership.TryGetValue(deviceId, out var names) ? names : Array.Empty<string>();

    public IReadOnlyList<string> GroupNames =>
        _membership.Values.SelectMany(n => n).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    public Task RefreshAsync()
    {
        if (_inFlight is { IsCompleted: false } running)
        {
            return running;
        }

        _inFlight = FetchAsync();
        return _inFlight;
    }

    public void Clear()
    {
        _generation++;
        _started = false;
        _timer.Stop();
        _membership = new Dictionary<int, IReadOnlyList<string>>();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task FetchAsync()
    {
        if (!_session.IsConnected)
        {
            return;
        }

        var generation = _generation;

        try
        {
            var membership = await _client.DeviceGroups.GetMembershipByDeviceAsync().ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                _membership = membership;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load device group membership");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load device group membership unexpectedly");
        }
    }

    private TimeSpan RefreshInterval()
        => TimeSpan.FromSeconds(_settings.Current.PollIntervalSeconds * RefreshMultiplier);
}

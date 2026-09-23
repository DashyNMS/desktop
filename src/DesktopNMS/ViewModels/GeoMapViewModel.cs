using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>A device listed under a pin in the Geographical map's details panel.</summary>
public sealed record GeoDeviceRow(int DeviceId, string Name, DeviceState State);

/// <summary>
/// One location pin on the Geographical map, with how many of its devices
/// are in each state. The pin shows the mix (see <see cref="Views.GeoMapCanvas"/>)
/// rather than just the worst device - a site with two of forty devices down
/// isn't a site that's offline.
/// </summary>
public sealed class GeoPin
{
    public GeoPin(LocationPin location, IReadOnlyList<GeoDeviceRow> devices)
    {
        Location = location;
        Devices = devices;
        World = location.World;
        UpCount = devices.Count(d => d.State == DeviceState.Up);
        DownCount = devices.Count(d => d.State == DeviceState.Down);
        MaintenanceCount = devices.Count(d => d.State == DeviceState.Maintenance);
    }

    public LocationPin Location { get; }

    public string Name => Location.Name;

    public IReadOnlyList<GeoDeviceRow> Devices { get; }

    public int DeviceCount => Devices.Count;

    public int UpCount { get; }

    public int DownCount { get; }

    public int MaintenanceCount { get; }

    /// <summary>Disabled or ignored - not being monitored, so neither up nor down.</summary>
    public int InactiveCount => DeviceCount - UpCount - DownCount - MaintenanceCount;

    public MapPoint World { get; }

    /// <summary>"40 devices · 2 down · 1 in maintenance"</summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string> { $"{DeviceCount} {(DeviceCount == 1 ? "device" : "devices")}" };
            if (DownCount > 0)
            {
                parts.Add($"{DownCount} down");
            }

            if (MaintenanceCount > 0)
            {
                parts.Add($"{MaintenanceCount} in maintenance");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>The Geographical map legend's colours - the same brushes the pins are drawn with.</summary>
public static class MapLegend
{
    public static System.Windows.Media.Brush Up => Converters.SeverityToBrushConverter.Ok;

    public static System.Windows.Media.Brush Down => Converters.SeverityToBrushConverter.Critical;

    public static System.Windows.Media.Brush Maintenance => Converters.SeverityToBrushConverter.Maintenance;

    public static System.Windows.Media.Brush Inactive => Converters.SeverityToBrushConverter.Unknown;
}

/// <summary>
/// The Maps tab's Geographical map: every LibreNMS location with
/// coordinates as a pin on a real map (OpenStreetMap tiles by default, as
/// LibreNMS's own Leaflet world map uses), for the whole fleet or one device
/// group. Device states come from the shared <see cref="DeviceMonitor"/>
/// poll; the location list from one <c>resources/locations</c> call on
/// first show and on Refresh.
/// </summary>
public sealed class GeoMapViewModel : ObservableObject, IDisposable
{
    private readonly DeviceMonitor _deviceMonitor;
    private readonly ILibreNmsClient _client;
    private readonly IDeviceGroupMembershipService _groupMembership;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILogger<GeoMapViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    private IReadOnlyList<Device> _devices = Array.Empty<Device>();
    private IReadOnlySet<int> _maintenanceIds = new HashSet<int>();
    private IReadOnlyList<Location>? _locations;
    private bool _hasDevices;

    private IReadOnlyList<GeoPin> _pins = Array.Empty<GeoPin>();
    private IReadOnlyList<GeoPin> _selectedPins = Array.Empty<GeoPin>();
    private MapScopeOption _selectedScope;
    private string _searchText = string.Empty;
    private int _unplacedCount;
    private bool _isLoading;
    private string? _errorMessage;
    private bool _hasFittedOnce;

    public GeoMapViewModel(
        DeviceMonitor deviceMonitor,
        ILibreNmsClient client,
        IDeviceGroupMembershipService groupMembership,
        IMapTileService tiles,
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        ILogger<GeoMapViewModel> logger)
    {
        _deviceMonitor = deviceMonitor;
        _client = client;
        _groupMembership = groupMembership;
        Tiles = tiles;
        _session = session;
        _settings = settings;
        _windows = windows;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _selectedScope = MapScopes.AllDevices;
        Scopes = new ObservableCollection<MapScopeOption> { _selectedScope };

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _session.IsConnected && !IsLoading);
        FitToViewCommand = new RelayCommand(() => FitToViewRequested?.Invoke(this, EventArgs.Empty));
        OpenDeviceCommand = new RelayCommand(p =>
        {
            if (p is GeoDeviceRow row)
            {
                _windows.ShowDeviceDetail(row.DeviceId);
            }
        });
        OpenAttributionCommand = new RelayCommand(() => _windows.OpenUrl(new Uri("https://www.openstreetmap.org/copyright")));
        ShowInDevicesCommand = new RelayCommand(() =>
        {
            if (_selectedPins.Count > 0)
            {
                _windows.ShowDevicesFilteredByLocations(_selectedPins.Select(p => p.Name).ToList());
            }
        });
        ClearFiltersCommand = new RelayCommand(() => SearchText = string.Empty);

        _deviceMonitor.Polled += OnDevicesPolled;
        _groupMembership.Changed += OnGroupMembershipChanged;
        _session.StateChanged += OnSessionStateChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public event EventHandler? FitToViewRequested;

    public event EventHandler<GeoPin>? CenterOnRequested;

    /// <summary>For the canvas to fetch tiles through.</summary>
    public IMapTileService Tiles { get; }

    public ObservableCollection<MapScopeOption> Scopes { get; }

    public MapScopeOption SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (value is not null && SetProperty(ref _selectedScope, value))
            {
                SelectedPins = Array.Empty<GeoPin>();
                Rebuild(fit: true);
                RaiseLoadingState();
            }
        }
    }

    public IReadOnlyList<GeoPin> Pins
    {
        get => _pins;
        private set
        {
            if (SetProperty(ref _pins, value))
            {
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>The clicked pin - or several, when pins drawn as one cluster were clicked together.</summary>
    public IReadOnlyList<GeoPin> SelectedPins
    {
        get => _selectedPins;
        set
        {
            if (SetProperty(ref _selectedPins, value ?? Array.Empty<GeoPin>()))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(ShowInDevicesLabel));
            }
        }
    }

    public bool HasSelection => _selectedPins.Count > 0;

    /// <summary>Opens the Devices tab filtered to the selected location(s) - every location under the pin, when it's a merged one.</summary>
    public RelayCommand ShowInDevicesCommand { get; }

    public string ShowInDevicesLabel => _selectedPins.Count > 1
        ? $"Show these {_selectedPins.Count} locations in Devices"
        : "Show in Devices";

    /// <summary>The tile server in use - the setting, or OpenStreetMap's standard tiles when that's unset or unusable.</summary>
    public string TileTemplate => TileUrlTemplate.Normalise(_settings.Current.MapTileUrl) ?? TileUrlTemplate.Default;

    public bool IsOpenStreetMap => TileUrlTemplate.IsOpenStreetMap(TileTemplate);

    /// <summary>Credit for a non-OpenStreetMap tile server - just which server it is.</summary>
    public string OtherTilesCredit => Uri.TryCreate(TileUrlTemplate.Format(TileTemplate, 0, 0, 0), UriKind.Absolute, out var uri)
        ? $"Map tiles: {uri.Host}"
        : string.Empty;

    /// <summary>"36 locations · 495 devices · 88 without coordinates"</summary>
    public string SummaryText
    {
        get
        {
            if (_pins.Count == 0 && _unplacedCount == 0)
            {
                return string.Empty;
            }

            var devices = _pins.Sum(p => p.DeviceCount);
            var text = $"{_pins.Count} {(_pins.Count == 1 ? "location" : "locations")} · {devices} {(devices == 1 ? "device" : "devices")}";
            return _unplacedCount > 0 ? $"{text} · {_unplacedCount} without coordinates" : text;
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                FindSearchMatch();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                RaiseLoadingState();
            }
        }
    }

    /// <summary>What the map is still waiting for - null once devices, locations and (for a group) membership are all in.</summary>
    public string? LoadingText =>
        !_session.IsConnected || HasError ? null :
        !_hasDevices ? "Loading devices..." :
        _isLoading || _locations is null ? "Loading locations..." :
        _selectedScope.GroupName is not null && !_groupMembership.HasLoaded ? "Loading device groups..." :
        null;

    public bool ShowLoading => LoadingText is not null;

    private void RaiseLoadingState()
    {
        OnPropertyChanged(nameof(LoadingText));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(IsEmpty));
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

    public bool IsEmpty => !ShowLoading && !HasError && _locations is not null && _hasDevices && _pins.Count == 0;

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand FitToViewCommand { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand OpenAttributionCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    public void OnShown()
    {
        _deviceMonitor.Start();
        _groupMembership.EnsureStarted();

        if (!_hasDevices)
        {
            _deviceMonitor.RequestRefresh();
        }

        if (_locations is null)
        {
            _ = LoadLocationsAsync();
        }
    }

    private async Task RefreshAsync()
    {
        _deviceMonitor.RequestRefresh();
        _ = _groupMembership.RefreshAsync();
        await LoadLocationsAsync().ConfigureAwait(true);
    }

    private async Task LoadLocationsAsync()
    {
        if (!_session.IsConnected)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            _locations = await _client.Locations.ListAsync().ConfigureAwait(true);
            Rebuild(fit: !_hasFittedOnce);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load locations for the geographical map");
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            _devices = result.Devices;
            _maintenanceIds = result.DeviceIdsUnderMaintenance;
            _hasDevices = true;
            Rebuild(fit: !_hasFittedOnce);
            RaiseLoadingState();
        });
    }

    private void OnGroupMembershipChanged(object? sender, EventArgs e)
    {
        var previousKey = _selectedScope.Key;

        if (MapScopes.Sync(Scopes, _groupMembership.GroupNames))
        {
            _selectedScope = Scopes.FirstOrDefault(s => s.Key == previousKey) ?? Scopes[0];
            OnPropertyChanged(nameof(SelectedScope));
        }

        if (_selectedScope.GroupName is not null)
        {
            Rebuild(fit: previousKey != _selectedScope.Key || _pins.Count == 0);
        }

        RaiseLoadingState();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        OnPropertyChanged(nameof(TileTemplate));
        OnPropertyChanged(nameof(IsOpenStreetMap));
        OnPropertyChanged(nameof(OtherTilesCredit));

        // The name style may have changed.
        Rebuild(fit: false);
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() =>
    {
        if (!_session.IsConnected)
        {
            _locations = null;
            _devices = Array.Empty<Device>();
            _hasDevices = false;
            _hasFittedOnce = false;
            SelectedPins = Array.Empty<GeoPin>();
            Pins = Array.Empty<GeoPin>();
        }

        RaiseLoadingState();
    });

    /// <summary>Cheap (tens of locations), so it simply rebuilds every pin on each poll - the canvas keeps its own view, so nothing moves.</summary>
    private void Rebuild(bool fit)
    {
        if (_locations is null || !_hasDevices)
        {
            return;
        }

        if (_selectedScope.GroupName is not null && !_groupMembership.HasLoaded)
        {
            // Without membership the group would look empty - wait (the
            // loading overlay says so) until OnGroupMembershipChanged rebuilds.
            Pins = Array.Empty<GeoPin>();
            RaiseLoadingState();
            return;
        }

        var scoped = _selectedScope.GroupName is { } group
            ? _devices.Where(d => _groupMembership.GroupsFor(d.DeviceId).Contains(group, StringComparer.OrdinalIgnoreCase))
            : _devices;

        var placement = GeoLocations.Build(scoped, _locations);
        var byId = _devices.ToDictionary(d => d.DeviceId);
        var nameStyle = _settings.Current.DeviceNameStyle;

        _unplacedCount = placement.UnplacedDeviceIds.Count;

        var pins = placement.Pins.Select(p => new GeoPin(p, p.DeviceIds
                .Select(id => byId[id])
                .Select(d => new GeoDeviceRow(
                    d.DeviceId,
                    nameStyle.Resolve(d, d.Hostname),
                    _maintenanceIds.Contains(d.DeviceId) ? DeviceState.Maintenance : d.State))
                .OrderBy(r => r.State == DeviceState.Down ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .ToList();

        // Keep the selection across a refresh, matched by location.
        var selectedIds = _selectedPins.Select(p => p.Location.LocationId).ToHashSet();
        Pins = pins;
        SelectedPins = pins.Where(p => selectedIds.Contains(p.Location.LocationId)).ToList();
        OnPropertyChanged(nameof(SummaryText));

        if (fit && pins.Count > 0)
        {
            _hasFittedOnce = true;
            FitToViewRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Finds a location - or the location of a device - by name, and centres on it.</summary>
    private void FindSearchMatch()
    {
        var term = _searchText.Trim();
        if (term.Length == 0)
        {
            return;
        }

        var match = _pins.FirstOrDefault(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            ?? _pins.FirstOrDefault(p => p.Devices.Any(d => d.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));

        if (match is not null)
        {
            SelectedPins = new[] { match };
            CenterOnRequested?.Invoke(this, match);
        }
    }

    public void Dispose()
    {
        _deviceMonitor.Polled -= OnDevicesPolled;
        _groupMembership.Changed -= OnGroupMembershipChanged;
        _session.StateChanged -= OnSessionStateChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}

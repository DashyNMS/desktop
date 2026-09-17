using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using DesktopNMS.Core.Api;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// View model behind the Locations tab: lists every LibreNMS location and
/// lets them be created/edited/deleted. Simpler than <see cref="GroupsViewModel"/>:
/// there is no static/dynamic split (every location is always fully
/// editable) and no membership API - a device counts as "at" a location
/// purely by its own free-text <see cref="Core.Models.Device.Location"/>
/// matching this location's name, so the Devices column is computed with a
/// single device-list fetch rather than one call per row.
/// </summary>
public sealed class LocationsViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly IWindowService _windows;
    private readonly ILogger<LocationsViewModel> _logger;

    private bool _hasLoadedOnce;
    private bool _isBusy;
    private string? _errorMessage;
    private string _searchText = string.Empty;

    public LocationsViewModel(
        ILibreNmsClient client,
        ISessionService session,
        IWindowService windows,
        ILogger<LocationsViewModel> logger)
    {
        _client = client;
        _session = session;
        _windows = windows;
        _logger = logger;

        Locations = new ObservableCollection<LocationListItemViewModel>();
        LocationsView = CollectionViewSource.GetDefaultView(Locations);
        LocationsView.Filter = FilterLocation;
        LocationsView.SortDescriptions.Add(new SortDescription(nameof(LocationListItemViewModel.Name), ListSortDirection.Ascending));

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => _session.IsConnected && !IsBusy);
        AddLocationCommand = new RelayCommand(AddLocation);
        ClearFiltersCommand = new RelayCommand(() => SearchText = string.Empty);
    }

    public ObservableCollection<LocationListItemViewModel> Locations { get; }

    public ICollectionView LocationsView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand AddLocationCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                LocationsView.Refresh();
                OnPropertyChanged(nameof(HasAnyFilterApplied));
                LoadState.UpdateVisibleCount(LocationsView.Cast<object>().Count());
            }
        }
    }

    public bool HasAnyFilterApplied => !string.IsNullOrEmpty(SearchText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
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

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasLocations => Locations.Count > 0;

    /// <summary>Drives the loading/empty/no-matches split on the grid (issue #16).</summary>
    public ListLoadState LoadState { get; } = new();

    /// <summary>Loads the location list once, lazily, the first time the tab is actually shown - same convention as every other tab.</summary>
    public void OnShown()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _hasLoadedOnce = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        LoadState.BeginLoad();

        try
        {
            var locations = await _client.Locations.ListAsync().ConfigureAwait(true);

            Locations.Clear();
            foreach (var location in locations.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                Locations.Add(new LocationListItemViewModel(location, ViewDevices, EditLocation, DeleteLocation));
            }

            OnPropertyChanged(nameof(HasLocations));

            // A second pass, not part of the row above: one Devices.ListAsync
            // call, matched client-side against each location's name - there
            // is no bulk "devices per location" endpoint, but unlike Device
            // Groups' per-group membership fetch this only ever costs one
            // request regardless of how many locations there are. Non-fatal
            // on failure - the Devices column just stays at "...".
            try
            {
                var devices = await _client.Devices.ListAsync().ConfigureAwait(true);
                var counts = devices
                    .Where(d => !string.IsNullOrWhiteSpace(d.Location))
                    .GroupBy(d => d.Location!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                foreach (var item in Locations)
                {
                    item.MemberCount = counts.TryGetValue(item.Name, out var count) ? count : 0;
                }
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogWarning(ex, "Could not load devices to count locations' members");
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load locations");
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
            LoadState.CompleteLoad(Locations.Count, LocationsView.Cast<object>().Count());
        }
    }

    private void AddLocation()
    {
        if (_windows.ShowAddLocationDialog())
        {
            _ = LoadAsync();
        }
    }

    private void EditLocation(LocationListItemViewModel item)
    {
        if (_windows.ShowEditLocationDialog(item.Location))
        {
            _ = LoadAsync();
        }
    }

    private void ViewDevices(LocationListItemViewModel item) => _windows.ShowDevicesFilteredByLocation(item.Name);

    private void DeleteLocation(LocationListItemViewModel item) => _ = DeleteLocationAsync(item);

    private async Task DeleteLocationAsync(LocationListItemViewModel item)
    {
        if (!_windows.Confirm("Delete location", $"Permanently delete the location '{item.Name}' from LibreNMS? This cannot be undone."))
        {
            return;
        }

        try
        {
            await _client.Locations.DeleteAsync(item.Location.Id).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not delete location {LocationName}", item.Name);
            _windows.ShowError("Delete failed", ex.ToUserMessage());
        }
    }

    private bool FilterLocation(object item)
    {
        if (item is not LocationListItemViewModel location)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(SearchText)
            || location.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

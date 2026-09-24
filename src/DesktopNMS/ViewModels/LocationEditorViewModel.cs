using System;
using System.Globalization;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the "Add/Edit location" dialog (see <see cref="Views.LocationEditorWindow"/>).
/// One class serves both modes, same shape as <see cref="DeviceGroupEditorViewModel"/> -
/// create mode is the default; <see cref="Initialize"/> switches it into edit
/// mode for a specific existing location. Unlike the group editor, there is
/// no device picker: LibreNMS has no bulk "assign these devices" endpoint
/// for locations, so a device is only ever tied to one via its own free-text
/// Location field (see the Edit Device screen).
/// </summary>
public sealed class LocationEditorViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ILogger<LocationEditorViewModel> _logger;

    /// <summary>Null in create mode. In edit mode, the location's id, used to address the PATCH/DELETE calls - by id rather than name, so a rename addresses the right location.</summary>
    private int? _originalId;

    private string _name = string.Empty;
    private string _latitude = string.Empty;
    private string _longitude = string.Empty;
    private bool _fixedCoordinates = true;
    private bool _isBusy;
    private string? _errorMessage;

    public LocationEditorViewModel(ILibreNmsClient client, ILogger<LocationEditorViewModel> logger)
    {
        _client = client;
        _logger = logger;

        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
    }

    /// <summary>Switches this dialog into edit mode for an existing location - called by <see cref="Services.WindowService.ShowEditLocationDialog"/> right after resolving this view model from DI and before the dialog is shown.</summary>
    public void Initialize(Location location)
    {
        _originalId = location.Id;
        Name = location.Name;
        Latitude = location.Latitude?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Longitude = location.Longitude?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        FixedCoordinates = location.FixedCoordinates;
    }

    public bool IsEditMode => _originalId is not null;

    public string Title => IsEditMode ? "Edit location" : "Add location";

    /// <summary>
    /// Only when creating: LibreNMS's add_location reads fixed_coordinates,
    /// but edit_location only fills location/lat/lng (see
    /// <see cref="ILocationsApi.UpdateAsync"/>), so an edit can't change it.
    /// </summary>
    public bool IsFixedCoordinatesEditable => !IsEditMode;

    public event EventHandler<bool>? RequestClose;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Latitude
    {
        get => _latitude;
        set
        {
            if (SetProperty(ref _latitude, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Longitude
    {
        get => _longitude;
        set
        {
            if (SetProperty(ref _longitude, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True (LibreNMS's own default once coordinates are set) keeps the coordinates fixed; false lets a device's own reported coordinates overwrite them.</summary>
    public bool FixedCoordinates
    {
        get => _fixedCoordinates;
        set => SetProperty(ref _fixedCoordinates, value);
    }

    public AsyncRelayCommand SaveCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
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

    private bool CanSave() =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(Name)
        && double.TryParse(Latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
        && double.TryParse(Longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private async Task SaveAsync()
    {
        ErrorMessage = null;

        if (!double.TryParse(Latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(Longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
        {
            ErrorMessage = "Latitude and longitude must both be valid numbers.";
            return;
        }

        IsBusy = true;

        try
        {
            if (_originalId is { } id)
            {
                await _client.Locations.UpdateAsync(id, Name.Trim(), lat, lng).ConfigureAwait(true);
            }
            else
            {
                await _client.Locations.CreateAsync(Name.Trim(), lat, lng, FixedCoordinates).ConfigureAwait(true);
            }

            RequestClose?.Invoke(this, true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not save location {LocationName}", Name);
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs Device Details' Network, Wireless section (#55): the device's
/// wireless readings - a controller's AP and client counts, a radio link's
/// signal, noise, rate, ... - one card per class, each with its LibreNMS
/// limits and a link to its graph. Loaded when the window opens, since the
/// nav item only shows for a device that has any.
/// </summary>
public sealed class WirelessSectionViewModel : ObservableObject
{
    private readonly int _deviceId;
    private readonly ILibreNmsClient _client;
    private readonly IAccessPointDirectory _accessPointDirectory;
    private readonly IDeviceCache _devices;
    private readonly ILogger _logger;
    private readonly CancellationToken _windowToken;
    private readonly Action<string> _showGraph;

    private bool _hasLoaded;
    private bool _isLoading;
    private string? _errorMessage;
    private bool _isLoadingAccessPoints;
    private string? _accessPointsError;

    /// <param name="showGraph">Opens the Graphs section on a graph, by name.</param>
    public WirelessSectionViewModel(
        int deviceId,
        ILibreNmsClient client,
        IAccessPointDirectory accessPoints,
        IDeviceCache devices,
        IWindowService windows,
        ILogger logger,
        CancellationToken windowToken,
        Action<string> showGraph)
    {
        _deviceId = deviceId;
        _client = client;
        _accessPointDirectory = accessPoints;
        _devices = devices;
        _logger = logger;
        _windowToken = windowToken;
        _showGraph = showGraph;

        Classes = new ObservableCollection<WirelessClassViewModel>();
        AccessPoints = new ObservableCollection<AccessPointItemViewModel>();
        ShowGraphCommand = new RelayCommand(parameter =>
        {
            if (parameter is WirelessClassViewModel item)
            {
                _showGraph(item.GraphName);
            }
        });
        OpenAccessPointCommand = new RelayCommand(parameter => windows.ShowAccessPoint((parameter as AccessPointItemViewModel)?.Name));
    }

    public ObservableCollection<WirelessClassViewModel> Classes { get; }

    public RelayCommand ShowGraphCommand { get; }

    /// <summary>
    /// For a controller (a device reporting an AP count): every AP the
    /// switches see over LLDP. LibreNMS's API doesn't say which controller
    /// an AP is joined to, so this is the fleet's list, not just this
    /// controller's - the card says so.
    /// </summary>
    public ObservableCollection<AccessPointItemViewModel> AccessPoints { get; }

    /// <summary>Opens the Access points page, on the given AP if there is one.</summary>
    public RelayCommand OpenAccessPointCommand { get; }

    public bool IsController => Classes.Any(c => c.SensorClass == WirelessSensorClasses.ApCount);

    public bool ShowAccessPointsCard => IsController && (HasAccessPoints || IsLoadingAccessPoints || HasAccessPointsError);

    public bool HasAccessPoints => AccessPoints.Count > 0;

    public bool IsLoadingAccessPoints
    {
        get => _isLoadingAccessPoints;
        private set
        {
            if (SetProperty(ref _isLoadingAccessPoints, value))
            {
                OnPropertyChanged(nameof(ShowAccessPointsCard));
            }
        }
    }

    public string? AccessPointsError
    {
        get => _accessPointsError;
        private set
        {
            if (SetProperty(ref _accessPointsError, value))
            {
                OnPropertyChanged(nameof(HasAccessPointsError));
                OnPropertyChanged(nameof(ShowAccessPointsCard));
            }
        }
    }

    public bool HasAccessPointsError => !string.IsNullOrEmpty(_accessPointsError);

    public bool HasAny => Classes.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseStateChanged();
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
                RaiseStateChanged();
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    /// <summary>Shows while loading and once there's something to show (or an error to see) - most devices have no radio.</summary>
    public bool ShowNav => !_hasLoaded || HasAny || HasError;

    public bool ShowEmptyMessage => _hasLoaded && !IsLoading && !HasAny && !HasError;

    /// <summary>"29 APs - 131 clients", or the number of readings for a device without either.</summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>();
            foreach (var cls in new[] { WirelessSensorClasses.ApCount, WirelessSensorClasses.Clients })
            {
                if (Classes.FirstOrDefault(c => c.SensorClass == cls) is { Total: { } total })
                {
                    var noun = cls == WirelessSensorClasses.ApCount ? "AP" : "client";
                    parts.Add(total.ToString("N0", CultureInfo.CurrentCulture) + " " + noun + (Math.Abs(total - 1) < 0.0001 ? string.Empty : "s"));
                }
            }

            if (parts.Count == 0 && HasAny)
            {
                var readings = Classes.Sum(c => c.Readings.Count);
                parts.Add(readings.ToString(CultureInfo.CurrentCulture) + (readings == 1 ? " reading" : " readings"));
            }

            return string.Join(" - ", parts);
        }
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var sensors = await _client.Devices.GetWirelessSensorsAsync(_deviceId, _windowToken).ConfigureAwait(true);

            Classes.Clear();
            foreach (var group in sensors
                .Where(s => !s.Deleted && !string.IsNullOrWhiteSpace(s.SensorClass))
                .GroupBy(s => s.SensorClass!.Trim().ToLowerInvariant())
                .OrderBy(g => WirelessSensorClasses.SortRank(g.Key))
                .ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                Classes.Add(new WirelessClassViewModel(group.Key, group.OrderBy(s => s.Description, StringComparer.OrdinalIgnoreCase).ToList()));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load wireless sensors for device {DeviceId}", _deviceId);
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            _hasLoaded = true;
            IsLoading = false;
        }

        RaiseStateChanged();

        if (IsController)
        {
            await LoadAccessPointsAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadAccessPointsAsync()
    {
        IsLoadingAccessPoints = true;
        AccessPointsError = null;

        try
        {
            var snapshot = await _accessPointDirectory.GetAsync(cancellationToken: _windowToken).ConfigureAwait(true);

            AccessPoints.Clear();
            foreach (var ap in snapshot.AccessPoints)
            {
                AccessPoints.Add(new AccessPointItemViewModel(ap, snapshot.PortOf(ap), _devices.Get(ap.SwitchDeviceId)));
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load access points for device {DeviceId}", _deviceId);
            AccessPointsError = ex.ToUserMessage();
        }
        finally
        {
            IsLoadingAccessPoints = false;
            OnPropertyChanged(nameof(HasAccessPoints));
            OnPropertyChanged(nameof(ShowAccessPointsCard));
        }
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasAny));
        OnPropertyChanged(nameof(ShowNav));
        OnPropertyChanged(nameof(ShowEmptyMessage));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsController));
        OnPropertyChanged(nameof(ShowAccessPointsCard));
    }
}

/// <summary>One card in the Wireless section: every reading of one class ("Clients", "SNR", ...) on the device.</summary>
public sealed class WirelessClassViewModel
{
    public WirelessClassViewModel(string sensorClass, IReadOnlyList<WirelessSensor> sensors)
    {
        SensorClass = sensorClass;
        Readings = sensors.Select(s => new WirelessReadingViewModel(s)).ToList();

        // Counts add up across readings (a controller may report clients per
        // band); a signal or a rate doesn't, so only counts get a total.
        var isCount = sensorClass is WirelessSensorClasses.ApCount or WirelessSensorClasses.Clients;
        var values = sensors.Where(s => s.Current is not null).Select(s => s.Current!.Value).ToList();
        Total = isCount && values.Count > 0 ? values.Sum() : null;
    }

    public string SensorClass { get; }

    public string Name => WirelessSensorClasses.NameOf(SensorClass);

    public string HeaderText => Readings.Count > 1 ? $"{Name} ({Readings.Count})" : Name;

    public string GraphName => WirelessSensorClasses.GraphName(SensorClass);

    public IReadOnlyList<WirelessReadingViewModel> Readings { get; }

    /// <summary>The sum of a count class's readings, shown in the header when there's more than one; null otherwise.</summary>
    public double? Total { get; }

    public string? TotalText => Total is { } total && Readings.Count > 1 ? "Total " + WirelessSensorClasses.Format(SensorClass, total) : null;
}

/// <summary>One wireless reading row.</summary>
public sealed class WirelessReadingViewModel
{
    public WirelessReadingViewModel(WirelessSensor sensor)
    {
        Sensor = sensor;
        Severity = WirelessFleet.Evaluate(sensor);
    }

    public WirelessSensor Sensor { get; }

    public string Description => string.IsNullOrWhiteSpace(Sensor.Description) ? WirelessSensorClasses.NameOf(Sensor.SensorClass) : Sensor.Description.Trim();

    public string ValueText => WirelessSensorClasses.Format(Sensor.SensorClass, Sensor.Current);

    public AlertSeverity Severity { get; }

    /// <summary>"Alerts below 22", "Alerts outside 10 - 40", or empty with no limits set.</summary>
    public string LimitsText
    {
        get
        {
            string F(double v) => WirelessSensorClasses.Format(Sensor.SensorClass, v);

            return (Sensor.LimitLow, Sensor.LimitHigh) switch
            {
                ({ } low, { } high) => $"Alerts outside {F(low)} - {F(high)}",
                ({ } low, null) => $"Alerts below {F(low)}",
                (null, { } high) => $"Alerts above {F(high)}",
                _ => string.Empty,
            };
        }
    }
}

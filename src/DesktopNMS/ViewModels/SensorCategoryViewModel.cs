using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One category's worth of sensor readings on the Health tab (dBm, signal,
/// temperature, fan speed, ...): its own list, filters, counts and selection.
/// The parent <see cref="HealthViewModel"/> owns the single shared fetch and
/// hands each category its slice of the results, so adding a category never
/// costs another API call.
/// </summary>
public sealed class SensorCategoryViewModel : ObservableObject
{
    private readonly IWindowService _windows;
    private readonly Func<AppSettings, IThresholdEvaluator> _thresholdsSelector;
    private readonly string _unitSuffix;
    private readonly Dictionary<int, SensorItemViewModel> _index = new();

    private SensorItemViewModel? _selectedSensor;
    private string _searchText = string.Empty;

    private bool _showCritical = true;
    private bool _showWarning = true;
    private bool _showOk = true;
    private bool _showUnknown = true;

    public SensorCategoryViewModel(
        IWindowService windows,
        Func<AppSettings, IThresholdEvaluator> thresholdsSelector,
        string unitSuffix,
        string noneFoundMessage)
    {
        _windows = windows;
        _thresholdsSelector = thresholdsSelector;
        _unitSuffix = unitSuffix;
        NoneFoundMessage = noneFoundMessage;

        Sensors = new ObservableCollection<SensorItemViewModel>();
        SensorsView = CollectionViewSource.GetDefaultView(Sensors);
        SensorsView.Filter = FilterSensor;

        OpenDeviceCommand = new RelayCommand(OpenSelectedDevice, () => SelectedSensor?.DeviceUrl is not null);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    /// <summary>Shown in the status bar when this category has no sensors at all.</summary>
    public string NoneFoundMessage { get; }

    public ObservableCollection<SensorItemViewModel> Sensors { get; }

    public ICollectionView SensorsView { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    // -------------------------------------------------------------- filtering

    public bool ShowCritical
    {
        get => _showCritical;
        set { if (SetProperty(ref _showCritical, value)) OnFilterChanged(); }
    }

    public bool ShowWarning
    {
        get => _showWarning;
        set { if (SetProperty(ref _showWarning, value)) OnFilterChanged(); }
    }

    public bool ShowOk
    {
        get => _showOk;
        set { if (SetProperty(ref _showOk, value)) OnFilterChanged(); }
    }

    /// <summary>Readings at or beyond a configured sentinel - typically an unplugged/faulty reading.</summary>
    public bool ShowUnknown
    {
        get => _showUnknown;
        set { if (SetProperty(ref _showUnknown, value)) OnFilterChanged(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) OnFilterChanged(); }
    }

    // ------------------------------------------------------------------ state

    public SensorItemViewModel? SelectedSensor
    {
        get => _selectedSensor;
        set
        {
            if (SetProperty(ref _selectedSensor, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OpenDeviceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => SelectedSensor is not null;

    public int CriticalCount => Sensors.Count(s => s.Severity == AlertSeverity.Critical);

    public int WarningCount => Sensors.Count(s => s.Severity == AlertSeverity.Warning);

    public int OkCount => Sensors.Count(s => s.Severity == AlertSeverity.Ok);

    /// <summary>Readings at or beyond a configured sentinel - "no data" rather than a real value.</summary>
    public int UnknownCount => Sensors.Count(s => s.Severity == AlertSeverity.Unknown);

    public int TotalCount => Sensors.Count;

    public int VisibleCount => SensorsView.Cast<object>().Count();

    /// <summary>True when sensors exist but the current filters hide all of them.</summary>
    public bool HasSensorsButNoneVisible => TotalCount > 0 && VisibleCount == 0;

    // --------------------------------------------------------------- updates

    /// <summary>Replaces this category's sensor list with a fresh fetch.</summary>
    public void Apply(IReadOnlyList<Sensor> sensors, Func<int, string> deviceNameFor, LibreNmsConnection? connection, AppSettings settings)
    {
        var thresholds = _thresholdsSelector(settings);

        var ordered = sensors
            .OrderBy(s => deviceNameFor(s.DeviceId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var incoming = ordered.Select(s => s.SensorId).ToHashSet();

        for (var i = Sensors.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Sensors[i].SensorId))
            {
                _index.Remove(Sensors[i].SensorId);
                Sensors.RemoveAt(i);
            }
        }

        for (var target = 0; target < ordered.Count; target++)
        {
            var sensor = ordered[target];
            var deviceName = deviceNameFor(sensor.DeviceId);

            if (_index.TryGetValue(sensor.SensorId, out var existing))
            {
                existing.Update(sensor, deviceName, connection, thresholds);

                var currentIndex = Sensors.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != target && target < Sensors.Count)
                {
                    Sensors.Move(currentIndex, target);
                }
            }
            else
            {
                var item = new SensorItemViewModel(sensor, deviceName, connection, thresholds, _unitSuffix);
                _index[sensor.SensorId] = item;
                Sensors.Insert(Math.Min(target, Sensors.Count), item);
            }
        }

        RaiseCountsChanged();

        if (SelectedSensor is not null && !_index.ContainsKey(SelectedSensor.SensorId))
        {
            SelectedSensor = null;
        }
    }

    /// <summary>Re-evaluates every row's severity, e.g. after the thresholds changed in Settings.</summary>
    public void ApplyThresholds(AppSettings settings)
    {
        var thresholds = _thresholdsSelector(settings);

        foreach (var sensor in Sensors)
        {
            sensor.ApplyThresholds(thresholds);
        }

        RaiseCountsChanged();
    }

    // --------------------------------------------------------------- commands

    private void OpenSelectedDevice()
    {
        if (SelectedSensor?.DeviceUrl is { } url)
        {
            _windows.OpenUrl(url);
        }
    }

    private void ClearFilters()
    {
        ShowCritical = true;
        ShowWarning = true;
        ShowOk = true;
        ShowUnknown = true;
        SearchText = string.Empty;
    }

    // ---------------------------------------------------------------- helpers

    private bool FilterSensor(object item)
    {
        if (item is not SensorItemViewModel sensor)
        {
            return false;
        }

        var severityAllowed = sensor.Severity switch
        {
            AlertSeverity.Critical => ShowCritical,
            AlertSeverity.Warning => ShowWarning,
            AlertSeverity.Ok => ShowOk,
            _ => ShowUnknown,
        };

        if (!severityAllowed)
        {
            return false;
        }

        var term = SearchText;
        return string.IsNullOrWhiteSpace(term) || sensor.Matches(term.Trim());
    }

    private void OnFilterChanged()
    {
        SensorsView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasSensorsButNoneVisible));
    }

    private void RaiseCountsChanged()
    {
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(OkCount));
        OnPropertyChanged(nameof(UnknownCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasSensorsButNoneVisible));

        // A threshold change can move rows in or out of the current filter
        // without the ObservableCollection itself changing.
        SensorsView.Refresh();
    }
}

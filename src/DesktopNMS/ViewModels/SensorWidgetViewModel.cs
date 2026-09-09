using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The "Sensors" dashboard widget: shows whichever sensors have been added to
/// it specifically (see <see cref="AddSensorCommand"/>/<see cref="RemoveSensorCommand"/>).
/// Each Sensors widget owns its own set - two widgets can show entirely
/// different sensors. <see cref="ApplyFleet"/> is called by the owning
/// <see cref="DashboardViewModel"/> after each fetch with every sensor across
/// the fleet, from which this widget picks out just its own members and the
/// candidates offered by its "Add sensor" picker.
/// </summary>
public sealed class SensorWidgetViewModel : DashboardWidgetViewModel
{
    private readonly Dictionary<int, SensorItemViewModel> _index = new();
    private readonly RelayCommand _openDeviceCommand;

    private HashSet<int> _ownedSensorIds;
    private IReadOnlyList<Sensor> _fleet = Array.Empty<Sensor>();
    private Func<int, string> _deviceNameFor = _ => string.Empty;
    private LibreNmsConnection? _connection;
    private AppSettings? _settings;
    private bool _isPickerOpen;
    private string _pickerSearchText = string.Empty;

    public SensorWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, RelayCommand openDeviceCommand)
        : base(layout, model)
    {
        _openDeviceCommand = openDeviceCommand;
        _ownedSensorIds = model.Sensors.Select(s => s.SensorId).ToHashSet();

        Sensors = new ObservableCollection<SensorItemViewModel>();
        PickerResults = new ObservableCollection<SensorPickerItem>();

        RemoveSensorCommand = new RelayCommand(parameter =>
        {
            if (parameter is SensorItemViewModel sensor)
            {
                Layout.RemoveSensor(Id, sensor.SensorId);
            }
        });

        TogglePickerCommand = new RelayCommand(() =>
        {
            IsPickerOpen = !IsPickerOpen;
            if (IsPickerOpen)
            {
                RefreshPicker();
            }
        });

        AddSensorCommand = new RelayCommand(parameter =>
        {
            if (parameter is SensorPickerItem item)
            {
                Layout.AddSensor(Id, item.Sensor, item.DeviceName);
            }
        });
    }

    public ObservableCollection<SensorItemViewModel> Sensors { get; }

    public bool HasSensors => Sensors.Count > 0;

    public RelayCommand OpenDeviceCommand => _openDeviceCommand;

    public RelayCommand RemoveSensorCommand { get; }

    public RelayCommand TogglePickerCommand { get; }

    public RelayCommand AddSensorCommand { get; }

    /// <summary>True while the "Add sensor" picker is showing in place of the sensor list.</summary>
    public bool IsPickerOpen
    {
        get => _isPickerOpen;
        set => SetProperty(ref _isPickerOpen, value);
    }

    public string PickerSearchText
    {
        get => _pickerSearchText;
        set
        {
            if (SetProperty(ref _pickerSearchText, value))
            {
                RefreshPicker();
            }
        }
    }

    public ObservableCollection<SensorPickerItem> PickerResults { get; }

    public bool HasPickerResults => PickerResults.Count > 0;

    public override void SyncFrom(DashboardWidget model)
    {
        base.SyncFrom(model);
        _ownedSensorIds = model.Sensors.Select(s => s.SensorId).ToHashSet();
        ApplyFleet(_fleet, _deviceNameFor, _connection, _settings);

        if (IsPickerOpen)
        {
            RefreshPicker();
        }
    }

    /// <summary>Closing the widget's edit mode always returns to the plain sensor list next time.</summary>
    protected override void OnEditingClosed() => IsPickerOpen = false;

    /// <summary>
    /// Called by <see cref="DashboardViewModel"/> after each fetch with every
    /// supported-class sensor across the fleet. Filters down to this widget's
    /// own members for display, and keeps the raw list around for the picker.
    /// </summary>
    public void ApplyFleet(IReadOnlyList<Sensor> fleet, Func<int, string> deviceNameFor, LibreNmsConnection? connection, AppSettings? settings)
    {
        _fleet = fleet;
        _deviceNameFor = deviceNameFor;
        _connection = connection;
        _settings = settings;

        if (settings is null)
        {
            return;
        }

        var owned = fleet
            .Where(s => _ownedSensorIds.Contains(s.SensorId))
            .OrderBy(s => deviceNameFor(s.DeviceId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var incoming = owned.Select(s => s.SensorId).ToHashSet();

        for (var i = Sensors.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Sensors[i].SensorId))
            {
                _index.Remove(Sensors[i].SensorId);
                Sensors.RemoveAt(i);
            }
        }

        for (var target = 0; target < owned.Count; target++)
        {
            var sensor = owned[target];
            var entry = SensorCategoryRegistry.Resolve(sensor.SensorClass);
            if (entry is null)
            {
                continue;
            }

            var deviceName = deviceNameFor(sensor.DeviceId);
            var thresholds = entry.Thresholds(settings);

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
                var item = new SensorItemViewModel(sensor, deviceName, connection, thresholds, entry.UnitSuffix);
                _index[sensor.SensorId] = item;
                Sensors.Insert(Math.Min(target, Sensors.Count), item);
            }
        }

        OnPropertyChanged(nameof(HasSensors));
    }

    /// <summary>Re-evaluates every row's severity, e.g. after the thresholds changed in Settings.</summary>
    public void ApplyThresholds(AppSettings settings)
    {
        foreach (var sensor in Sensors)
        {
            var entry = SensorCategoryRegistry.Resolve(sensor.Model.SensorClass);
            if (entry is not null)
            {
                sensor.ApplyThresholds(entry.Thresholds(settings));
            }
        }
    }

    private void RefreshPicker()
    {
        PickerResults.Clear();

        var term = PickerSearchText?.Trim();

        var candidates = _fleet
            .Where(s => !_ownedSensorIds.Contains(s.SensorId) && SensorCategoryRegistry.Resolve(s.SensorClass) is not null)
            .Select(s => new SensorPickerItem(s, _deviceNameFor(s.DeviceId)))
            .Where(p => string.IsNullOrEmpty(term) || p.Matches(term))
            .OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Description, StringComparer.OrdinalIgnoreCase)
            .Take(50);

        foreach (var candidate in candidates)
        {
            PickerResults.Add(candidate);
        }

        OnPropertyChanged(nameof(HasPickerResults));
    }
}

/// <summary>One row in a Sensors widget's "Add sensor" picker.</summary>
public sealed class SensorPickerItem
{
    public SensorPickerItem(Sensor sensor, string deviceName)
    {
        Sensor = sensor;
        DeviceName = deviceName;
    }

    public Sensor Sensor { get; }

    public int SensorId => Sensor.SensorId;

    public string DeviceName { get; }

    public string Description => string.IsNullOrWhiteSpace(Sensor.Description) ? "-" : Sensor.Description!;

    public bool Matches(string term) =>
        DeviceName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(term, StringComparison.OrdinalIgnoreCase);
}

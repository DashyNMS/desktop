using System;
using System.Globalization;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in a Health tab category's sensor list.</summary>
public sealed class SensorItemViewModel : ObservableObject
{
    private readonly string _unitSuffix;

    private Sensor _sensor;
    private string _deviceName;
    private LibreNmsConnection? _connection;
    private AlertSeverity _severity;

    public SensorItemViewModel(Sensor sensor, string deviceName, LibreNmsConnection? connection, IThresholdEvaluator thresholds, string unitSuffix)
    {
        _sensor = sensor;
        _deviceName = deviceName;
        _connection = connection;
        _unitSuffix = unitSuffix;
        _severity = thresholds.Evaluate(sensor.Current);
    }

    /// <summary>The underlying reading, for code (e.g. pinning) that needs the raw sensor fields.</summary>
    public Sensor Model => _sensor;

    public int SensorId => _sensor.SensorId;

    public int DeviceId => _sensor.DeviceId;

    public string DeviceName => _deviceName;

    public string Description => string.IsNullOrWhiteSpace(_sensor.Description) ? "-" : _sensor.Description!;

    /// <summary>The sensor class, translated to plain English where a translation is known - e.g. "fanspeed" reads as "Fan speed".</summary>
    public string ClassDisplayText => SensorClassDisplay.Resolve(_sensor.SensorClass);

    /// <summary>
    /// The component this sensor sits on, which a device's sensor list groups
    /// by - see <see cref="SensorGrouping"/> for how it is derived.
    /// </summary>
    public string GroupKey => SensorGrouping.ExtractGroupKey(Description);

    /// <summary>
    /// What distinguishes this row from its siblings in the same group: the
    /// measurement on its own (e.g. "Lane 1 RX Power" once "1/1/15" is
    /// stripped), or the sensor class when the whole description is the group
    /// key - the case for a group of identically-named sensors, where the
    /// class is the only thing telling the rows apart.
    /// </summary>
    public string RowLabel
    {
        get
        {
            var measurement = SensorGrouping.ExtractMeasurementLabel(Description, GroupKey);
            return measurement.Length == 0 ? ClassDisplayText : measurement;
        }
    }

    public double Value => _sensor.Current;

    public string ValueText => _sensor.Current.ToString("0.###", CultureInfo.InvariantCulture) + _unitSuffix;

    public AlertSeverity Severity => _severity;

    public string SeverityText => Severity == AlertSeverity.Unknown ? "No data" : Severity.ToDisplayString();

    public DateTime? LastUpdate => _sensor.LastUpdate;

    // Not converted from server time: LibreNMS's own local/UTC ambiguity is
    // only resolved for alert timestamps (see AlertDisplayContext), and a
    // sensor's staleness is what matters here, not the exact clock time.
    public string LastUpdateText => _sensor.LastUpdate is { } lastUpdate
        ? lastUpdate.ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)
        : "-";

    public Uri? DeviceUrl => _connection?.DeviceUrl(DeviceId);

    /// <summary>Everything a search box should match against.</summary>
    public bool Matches(string term) =>
        DeviceName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        || SensorId.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal);

    /// <summary>Replaces the underlying reading in place so the selection survives a refresh.</summary>
    public void Update(Sensor sensor, string deviceName, LibreNmsConnection? connection, IThresholdEvaluator thresholds)
    {
        _sensor = sensor;
        _deviceName = deviceName;
        _connection = connection;
        ApplyThresholds(thresholds);

        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(LastUpdate));
        OnPropertyChanged(nameof(LastUpdateText));
        OnPropertyChanged(nameof(DeviceUrl));
    }

    /// <summary>Re-evaluates severity only, e.g. after the thresholds changed in Settings.</summary>
    public void ApplyThresholds(IThresholdEvaluator thresholds)
    {
        var severity = thresholds.Evaluate(_sensor.Current);
        if (severity == _severity)
        {
            return;
        }

        _severity = severity;
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(SeverityText));
    }
}

/// <summary>
/// Plain-English names for LibreNMS sensor classes beyond the four with
/// configured thresholds (see <see cref="SensorCategoryRegistry"/>) - mainly
/// so a device's full sensor list (which shows every class, not just those
/// four) does not read as raw database enum values.
/// </summary>
file static class SensorClassDisplay
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dbm"] = "dBm",
        ["signal"] = "Signal",
        ["temperature"] = "Temperature",
        ["fanspeed"] = "Fan speed",
        ["voltage"] = "Voltage",
        ["current"] = "Current",
        ["power"] = "Power",
        ["frequency"] = "Frequency",
        ["humidity"] = "Humidity",
        ["state"] = "State",
        ["runtime"] = "Runtime",
        ["storage"] = "Storage",
        ["count"] = "Count",
        ["load"] = "Load",
        ["charge"] = "Charge",
        ["waterflow"] = "Water flow",
    };

    public static string Resolve(string? sensorClass) =>
        string.IsNullOrWhiteSpace(sensorClass) ? "-" : Names.GetValueOrDefault(sensorClass, sensorClass);
}

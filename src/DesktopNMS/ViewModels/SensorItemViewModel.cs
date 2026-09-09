using System;
using System.Globalization;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in the Health tab's sensor list.</summary>
public sealed class SensorItemViewModel : ObservableObject
{
    private Sensor _sensor;
    private string _deviceName;
    private LibreNmsConnection? _connection;
    private AlertSeverity _severity;

    public SensorItemViewModel(Sensor sensor, string deviceName, LibreNmsConnection? connection, DbmThresholdSettings thresholds)
    {
        _sensor = sensor;
        _deviceName = deviceName;
        _connection = connection;
        _severity = Evaluate(sensor, thresholds);
    }

    public int SensorId => _sensor.SensorId;

    public int DeviceId => _sensor.DeviceId;

    public string DeviceName => _deviceName;

    public string Description => string.IsNullOrWhiteSpace(_sensor.Description) ? "-" : _sensor.Description!;

    public double Value => _sensor.Current;

    public string ValueText => _sensor.Current.ToString("0.###", CultureInfo.InvariantCulture) + " dBm";

    public AlertSeverity Severity => _severity;

    public string SeverityText => Severity == AlertSeverity.Unknown ? "No signal" : Severity.ToDisplayString();

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
    public void Update(Sensor sensor, string deviceName, LibreNmsConnection? connection, DbmThresholdSettings thresholds)
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
    public void ApplyThresholds(DbmThresholdSettings thresholds)
    {
        var severity = Evaluate(_sensor, thresholds);
        if (severity == _severity)
        {
            return;
        }

        _severity = severity;
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(SeverityText));
    }

    private static AlertSeverity Evaluate(Sensor sensor, DbmThresholdSettings thresholds)
        => sensor.IsDbm ? thresholds.Evaluate(sensor.Current) : AlertSeverity.Unknown;
}

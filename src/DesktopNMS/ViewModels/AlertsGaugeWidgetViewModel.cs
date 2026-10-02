using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The "Alerts gauge" dashboard widget: three ring gauges - unacknowledged
/// critical, unacknowledged warning, and acknowledged (of any severity) - as
/// a share of all active (non-recovered) alerts. Like
/// <see cref="AlertsWidgetViewModel"/>, it listens to the app-wide
/// <see cref="AlertMonitor"/> rather than polling on its own.
/// </summary>
public sealed class AlertsGaugeWidgetViewModel : DashboardWidgetViewModel, IDisposable
{
    private readonly AlertMonitor _monitor;
    private readonly Dispatcher _dispatcher;

    private int _criticalCount;
    private int _warningCount;
    private int _acknowledgedCount;
    private int _totalActive;

    public AlertsGaugeWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, AlertMonitor monitor, IWindowService windows)
        : base(layout, model)
    {
        _monitor = monitor;

        // Each count opens that list on the Alerts tab (#212).
        OpenCriticalCommand = new RelayCommand(() => windows.ShowAlertsWithSeverity(AlertSeverity.Critical));
        OpenWarningCommand = new RelayCommand(() => windows.ShowAlertsWithSeverity(AlertSeverity.Warning));
        OpenAllCommand = new RelayCommand(() => windows.ShowAlertsWithSeverity(null));
        _dispatcher = Dispatcher.CurrentDispatcher;

        _monitor.Polled += OnPolled;

        // The monitor keeps no cached result for a late subscriber, so a
        // freshly-added widget would otherwise sit at zero until the next
        // scheduled poll (up to PollIntervalSeconds away).
        _monitor.RequestRefresh();
    }

    /// <summary>Critical alerts not yet acknowledged.</summary>
    public int CriticalCount => _criticalCount;

    /// <summary>Warning alerts not yet acknowledged.</summary>
    public int WarningCount => _warningCount;

    /// <summary>Alerts of any severity that have been acknowledged.</summary>
    public int AcknowledgedCount => _acknowledgedCount;

    public int TotalActive => _totalActive;

    /// <summary>"1 active alert", "12 active alerts".</summary>
    public string TotalActiveText => _totalActive == 1 ? "1 active alert" : _totalActive.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " active alerts";

    public RelayCommand OpenCriticalCommand { get; }

    public RelayCommand OpenWarningCommand { get; }

    /// <summary>The Acknowledged count: every alert, acknowledged included - the Alerts tab has no "acknowledged only".</summary>
    public RelayCommand OpenAllCommand { get; }

    /// <summary>Unacknowledged critical's share of all active alerts, for the ring gauge's arc.</summary>
    public double CriticalFraction => _totalActive > 0 ? (double)_criticalCount / _totalActive : 0;

    /// <summary>Unacknowledged warning's share of all active alerts, for the ring gauge's arc.</summary>
    public double WarningFraction => _totalActive > 0 ? (double)_warningCount / _totalActive : 0;

    /// <summary>Acknowledged's share of all active alerts, for the ring gauge's arc.</summary>
    public double AcknowledgedFraction => _totalActive > 0 ? (double)_acknowledgedCount / _totalActive : 0;

    public bool HasNoActiveAlerts => _totalActive == 0;

    private void OnPolled(object? sender, AlertPollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() => Apply(result.Alerts));
    }

    private void Apply(IReadOnlyList<Alert> alerts)
    {
        var active = alerts.Where(a => a.State != AlertState.Recovered).ToList();

        _acknowledgedCount = active.Count(a => a.IsAcknowledged);
        _criticalCount = active.Count(a => a.Severity == AlertSeverity.Critical && !a.IsAcknowledged);
        _warningCount = active.Count(a => a.Severity == AlertSeverity.Warning && !a.IsAcknowledged);
        _totalActive = active.Count;

        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(AcknowledgedCount));
        OnPropertyChanged(nameof(TotalActive));
        OnPropertyChanged(nameof(TotalActiveText));
        OnPropertyChanged(nameof(CriticalFraction));
        OnPropertyChanged(nameof(WarningFraction));
        OnPropertyChanged(nameof(AcknowledgedFraction));
        OnPropertyChanged(nameof(HasNoActiveAlerts));
    }

    public void Dispose() => _monitor.Polled -= OnPolled;
}

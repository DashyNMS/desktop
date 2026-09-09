using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The "Alerts" dashboard widget: a compact, always-current feed of alerts,
/// filtered by severity and whether to include acknowledged ones (see
/// <see cref="ShowCritical"/>/<see cref="ShowWarning"/>/<see cref="IncludeAcknowledged"/>,
/// each independent per widget instance). Rather than polling itself, it
/// subscribes to the app-wide <see cref="AlertMonitor"/> that the Alerts tab
/// also listens to, so adding this widget costs no extra API traffic.
/// </summary>
public sealed class AlertsWidgetViewModel : DashboardWidgetViewModel, IDisposable
{
    private readonly AlertMonitor _monitor;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, AlertItemViewModel> _index = new();

    private IReadOnlyList<Alert> _lastAlerts = Array.Empty<Alert>();
    private bool _showCritical;
    private bool _showWarning;
    private bool _includeAcknowledged;

    public AlertsWidgetViewModel(
        IDashboardLayoutService layout,
        DashboardWidget model,
        AlertMonitor monitor,
        ISessionService session,
        ISettingsStore settings,
        IDeviceCache devices,
        IWindowService windows)
        : base(layout, model)
    {
        _monitor = monitor;
        _session = session;
        _settings = settings;
        _devices = devices;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _showCritical = model.AlertsShowCritical;
        _showWarning = model.AlertsShowWarning;
        _includeAcknowledged = model.AlertsIncludeAcknowledged;

        Alerts = new ObservableCollection<AlertItemViewModel>();

        OpenAlertCommand = new RelayCommand(parameter =>
        {
            if (parameter is AlertItemViewModel { AlertUrl: { } url })
            {
                windows.OpenUrl(url);
            }
        });

        OpenDeviceCommand = new RelayCommand(parameter =>
        {
            if (parameter is AlertItemViewModel { DeviceUrl: { } url })
            {
                windows.OpenUrl(url);
            }
        });

        _monitor.Polled += OnPolled;

        // The monitor does not cache its last result for a late subscriber, so
        // a freshly-added widget would otherwise sit empty until the next
        // scheduled poll (up to PollIntervalSeconds away).
        _monitor.RequestRefresh();
    }

    public ObservableCollection<AlertItemViewModel> Alerts { get; }

    public bool HasAlerts => Alerts.Count > 0;

    public RelayCommand OpenAlertCommand { get; }

    public RelayCommand OpenDeviceCommand { get; }

    /// <summary>Show unacknowledged critical alerts (and, if <see cref="IncludeAcknowledged"/>, acknowledged ones too).</summary>
    public bool ShowCritical
    {
        get => _showCritical;
        set
        {
            if (SetProperty(ref _showCritical, value))
            {
                PersistFilter();
                Render();
            }
        }
    }

    /// <summary>Show unacknowledged warning alerts (and, if <see cref="IncludeAcknowledged"/>, acknowledged ones too).</summary>
    public bool ShowWarning
    {
        get => _showWarning;
        set
        {
            if (SetProperty(ref _showWarning, value))
            {
                PersistFilter();
                Render();
            }
        }
    }

    /// <summary>Whether an acknowledged alert still counts under its severity toggle above.</summary>
    public bool IncludeAcknowledged
    {
        get => _includeAcknowledged;
        set
        {
            if (SetProperty(ref _includeAcknowledged, value))
            {
                PersistFilter();
                Render();
            }
        }
    }

    public override void SyncFrom(DashboardWidget model)
    {
        base.SyncFrom(model);

        if (SetProperty(ref _showCritical, model.AlertsShowCritical, nameof(ShowCritical))
            | SetProperty(ref _showWarning, model.AlertsShowWarning, nameof(ShowWarning))
            | SetProperty(ref _includeAcknowledged, model.AlertsIncludeAcknowledged, nameof(IncludeAcknowledged)))
        {
            Render();
        }
    }

    private void PersistFilter() => Layout.SetAlertsFilter(Id, ShowCritical, ShowWarning, IncludeAcknowledged);

    private void OnPolled(object? sender, AlertPollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            _lastAlerts = result.Alerts;
            Render();
        });
    }

    /// <summary>Re-applies the current filter to the last-seen alerts. Cheap - no API call.</summary>
    private void Render()
    {
        var context = AlertDisplayContext.Create(_settings.Current, _session.Connection, _devices);

        var filtered = _lastAlerts
            .Where(PassesFilter)
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.Timestamp)
            .ToList();

        var incoming = filtered.Select(a => a.Id).ToHashSet();

        for (var i = Alerts.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Alerts[i].Id))
            {
                _index.Remove(Alerts[i].Id);
                Alerts.RemoveAt(i);
            }
        }

        for (var target = 0; target < filtered.Count; target++)
        {
            var alert = filtered[target];

            if (_index.TryGetValue(alert.Id, out var existing))
            {
                existing.Update(alert, context);

                var currentIndex = Alerts.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != target && target < Alerts.Count)
                {
                    Alerts.Move(currentIndex, target);
                }
            }
            else
            {
                var item = new AlertItemViewModel(alert, context);
                _index[alert.Id] = item;
                Alerts.Insert(Math.Min(target, Alerts.Count), item);
            }
        }

        OnPropertyChanged(nameof(HasAlerts));
    }

    private bool PassesFilter(Alert alert)
    {
        if (alert.State == AlertState.Recovered)
        {
            return false;
        }

        if (alert.IsAcknowledged && !IncludeAcknowledged)
        {
            return false;
        }

        return alert.Severity switch
        {
            AlertSeverity.Critical => ShowCritical,
            AlertSeverity.Warning => ShowWarning,
            _ => true,
        };
    }

    public void Dispose() => _monitor.Polled -= OnPolled;
}

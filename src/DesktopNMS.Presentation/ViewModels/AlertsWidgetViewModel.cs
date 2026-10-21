using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopNMS.Core.Alerting;
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
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<AlertGroupKey, AlertItemViewModel> _index = new();

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
        _dispatcher = UiThread.Dispatcher;

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

        // Clicking a row stays in the app: Device Details, as on the Alerts tab.
        OpenDeviceDetailsCommand = new RelayCommand(parameter =>
        {
            if (parameter is AlertItemViewModel item)
            {
                windows.ShowDeviceDetail(item.DeviceId);
            }
        });

        Header = new AlertsWidgetHeader(this);

        _monitor.Polled += OnPolled;

        // The monitor does not cache its last result for a late subscriber, so
        // a freshly-added widget would otherwise sit empty until the next
        // scheduled poll (up to PollIntervalSeconds away).
        _monitor.RequestRefresh();
    }

    public ObservableCollection<AlertItemViewModel> Alerts { get; }

    /// <summary>Drives the loading/empty/no-matches split on this widget (issue #16) - IsNoMatches is meaningful here, unlike most other widgets, since this one has its own severity/acknowledged filter.</summary>
    public ListLoadState LoadState { get; } = new();

    public RelayCommand OpenAlertCommand { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand OpenDeviceDetailsCommand { get; }

    /// <summary>The title bar's counted severity toggles (#212).</summary>
    public override object? HeaderOptions => Header;

    public AlertsWidgetHeader Header { get; }

    /// <summary>Unacknowledged critical alerts - the count on the title bar's critical toggle, whatever the filter.</summary>
    public int CriticalCount { get; private set; }

    public int WarningCount { get; private set; }

    public int AcknowledgedCount { get; private set; }

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

    /// <summary>Shift-click on a severity chip: show only that severity, hiding the other.</summary>
    public void IsolateSeverity(AlertSeverity severity)
    {
        _showCritical = severity == AlertSeverity.Critical;
        _showWarning = severity == AlertSeverity.Warning;

        OnPropertyChanged(nameof(ShowCritical));
        OnPropertyChanged(nameof(ShowWarning));
        PersistFilter();
        Render();
    }

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

    /// <summary>Re-applies the current filter to the last-seen alerts. Cheap - no API call. One row per device and rule (#353).</summary>
    private void Render()
    {
        var context = AlertDisplayContext.Create(_settings.Current, _session.Connection, _devices);

        var groups = AlertGrouping.ByDeviceAndRule(_lastAlerts.Where(a => a.State != AlertState.Recovered));
        var filtered = groups
            .Where(PassesFilter)
            .OrderByDescending(g => g.Severity)
            .ThenByDescending(g => g.LastChanged)
            .ToList();

        var incoming = filtered.Select(g => g.Key).ToHashSet();

        for (var i = Alerts.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Alerts[i].Key))
            {
                _index.Remove(Alerts[i].Key);
                Alerts.RemoveAt(i);
            }
        }

        for (var target = 0; target < filtered.Count; target++)
        {
            var group = filtered[target];

            if (_index.TryGetValue(group.Key, out var existing))
            {
                existing.Update(group, context);

                var currentIndex = Alerts.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != target && target < Alerts.Count)
                {
                    Alerts.Move(currentIndex, target);
                }
            }
            else
            {
                var item = new AlertItemViewModel(group, context);
                _index[group.Key] = item;
                Alerts.Insert(Math.Min(target, Alerts.Count), item);
            }
        }

        // Recovered alerts are never shown here regardless of the severity/
        // acknowledged toggles, so they don't count as "there's data the
        // filter is hiding" - only open groups count toward the
        // empty-vs-no-matches distinction.
        CriticalCount = groups.Count(g => g.State != AlertState.Acknowledged && g.Severity == AlertSeverity.Critical);
        WarningCount = groups.Count(g => g.State != AlertState.Acknowledged && g.Severity == AlertSeverity.Warning);
        AcknowledgedCount = groups.Count(g => g.State == AlertState.Acknowledged);
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(AcknowledgedCount));
        LoadState.CompleteLoad(groups.Count, Alerts.Count);
    }

    private bool PassesFilter(AlertGroup group)
    {
        // A group still needing attention shows under its severity even with
        // acknowledged ones hidden - a new fault beside acknowledged ones counts.
        if (group.State == AlertState.Acknowledged && !IncludeAcknowledged)
        {
            return false;
        }

        return group.Severity switch
        {
            AlertSeverity.Critical => ShowCritical,
            AlertSeverity.Warning => ShowWarning,
            _ => true,
        };
    }
    public void Dispose() => _monitor.Polled -= OnPolled;
}

/// <summary>The Alerts widget's title-bar controls: Critical, Warning and Acknowledged toggles with their counts (#212). Its own type so the title bar templates it without re-drawing the widget.</summary>
public sealed class AlertsWidgetHeader
{
    public AlertsWidgetHeader(AlertsWidgetViewModel widget) => Widget = widget;

    public AlertsWidgetViewModel Widget { get; }
}

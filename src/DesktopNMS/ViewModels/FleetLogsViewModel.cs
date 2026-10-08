using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Logs;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>A choice in the Logs tab's Device filter: one device, or (null) every device.</summary>
public sealed record LogDeviceOption(int? DeviceId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A choice in the Logs tab's Time filter. LibreNMS can't ask by time, so it narrows what's loaded.</summary>
public sealed record LogRangeOption(string Label, TimeSpan? Span)
{
    public override string ToString() => Label;
}

/// <summary>A choice in the alert log's State filter: one state, or (null) every state.</summary>
public sealed record LogStateOption(AlertState? State, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One event log row on the Logs tab.</summary>
public sealed class FleetEventRow
{
    public FleetEventRow(EventLogEntry entry, string device, DateTime? local)
    {
        Entry = entry;
        DeviceName = device;
        Local = local;
    }

    public EventLogEntry Entry { get; }

    public DateTime? Local { get; }

    public string TimeText => Local?.ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture) ?? "-";

    public string DeviceName { get; }

    public string TypeText => string.IsNullOrWhiteSpace(Entry.Type) ? "-" : Entry.Type!;

    public string Message => string.IsNullOrWhiteSpace(Entry.Message) ? "-" : Entry.Message!;

    public string Username => string.IsNullOrWhiteSpace(Entry.Username) ? "-" : Entry.Username!;
}

/// <summary>One alert log row on the Logs tab.</summary>
public sealed class FleetAlertRow
{
    private readonly AlertDetail _detail;

    public FleetAlertRow(AlertLogEntry entry, string device, DateTime? local, AlertRule? rule)
    {
        Entry = entry;
        DeviceName = device;
        Local = local;
        RuleName = rule?.Name ?? $"Rule {entry.RuleId}";
        Severity = rule?.Severity ?? AlertSeverity.Unknown;
        _detail = AlertFaultParser.Parse(entry);
    }

    public AlertLogEntry Entry { get; }

    public DateTime? Local { get; }

    public string TimeText => Local?.ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture) ?? "-";

    public string DeviceName { get; }

    public string RuleName { get; }

    public AlertSeverity Severity { get; }

    public string SeverityText => Severity == AlertSeverity.Unknown ? "-" : Severity.ToDisplayString();

    public AlertState State => Entry.State;

    public string StateText => State == AlertState.Active ? "Alerted" : State.ToDisplayString();

    /// <summary>What matched, e.g. "Gi0/1 - ifOperStatus = down" - empty when LibreNMS recorded none.</summary>
    public string DetailText
    {
        get
        {
            if (!_detail.HasFaults)
            {
                return string.Empty;
            }

            var fault = _detail.Faults[0];
            var fields = fault.PrimaryFields.Count > 0 ? fault.PrimaryFields : fault.Fields;
            var suffix = fields.Count > 0 ? " - " + string.Join(", ", fields.Take(3).Select(f => $"{f.Name} = {f.Value}")) : string.Empty;
            var more = _detail.Faults.Count > 1 ? $" (+{_detail.Faults.Count - 1} more)" : string.Empty;
            return fault.Title + suffix + more;
        }
    }
}

/// <summary>
/// The Logs tab's Event log and Alert log (#287): LibreNMS's logs for every
/// device, as DashyNMS Mobile's Logs page has them. The API filters by device
/// but not by type, state, text or time, so the Device filter asks the server
/// and the rest narrow what's loaded - a type or a search fetches a bigger
/// page first (Core's <see cref="EventLogFeed"/>), so a rare type isn't lost.
/// "Load more" asks for a bigger page, as the endpoints have no offset.
/// </summary>
public sealed class FleetLogsViewModel : ObservableObject
{
    internal const int PageSize = 100;
    internal const int MaxEntries = 1000;

    private const string AnyType = "Any type";

    private readonly ILibreNmsClient _client;
    private readonly IDeviceCache _devices;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILogger _logger;

    private IReadOnlyList<EventLogEntry> _events = Array.Empty<EventLogEntry>();
    private IReadOnlyList<AlertLogEntry> _alerts = Array.Empty<AlertLogEntry>();
    private IReadOnlyDictionary<int, AlertRule> _rules = new Dictionary<int, AlertRule>();
    private CancellationTokenSource? _loadCts;
    private bool _showingEventLog = true;
    private int _eventLimit = PageSize;
    private int _alertLimit = PageSize;
    private bool _eventsLoaded;
    private bool _alertsLoaded;
    private bool _eventsExhausted;
    private bool _alertsExhausted;
    private bool _isLoading;
    private string? _errorMessage;
    private string _searchText = string.Empty;
    private LogDeviceOption _selectedDevice;
    private string _selectedType = AnyType;
    private LogStateOption _selectedState;
    private LogRangeOption _selectedRange;
    private FleetEventRow? _selectedEvent;
    private FleetAlertRow? _selectedAlert;

    public FleetLogsViewModel(ILibreNmsClient client, IDeviceCache devices, ISettingsStore settings, IWindowService windows, ILogger logger)
    {
        _client = client;
        _devices = devices;
        _settings = settings;
        _windows = windows;
        _logger = logger;

        _selectedDevice = AllDevices;
        DeviceOptions.Add(AllDevices);
        TypeOptions.Add(AnyType);
        _selectedState = StateOptions[0];
        _selectedRange = RangeOptions[0];

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(reset: false), () => !IsLoading);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync, () => CanLoadMore);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => HasActiveFilters);
        OpenSelectedCommand = new RelayCommand(OpenSelected);
        ShowEventLogCommand = new RelayCommand(() => ShowingEventLog = true);
        ShowAlertLogCommand = new RelayCommand(() => ShowingEventLog = false);

        EventsCsv = new CsvExport(
            "event-log",
            new[] { "Time", "Device", "Type", "Message", "User" },
            () => Events.Select(e => CsvExport.Row(e.TimeText, e.DeviceName, e.TypeText, e.Message, e.Username)));
        AlertsCsv = new CsvExport(
            "alert-log",
            new[] { "Time", "Device", "Rule", "Severity", "State", "Detail" },
            () => Alerts.Select(a => CsvExport.Row(a.TimeText, a.DeviceName, a.RuleName, a.SeverityText, a.StateText, a.DetailText)));
    }

    private static LogDeviceOption AllDevices { get; } = new(null, "All devices");

    public static IReadOnlyList<LogRangeOption> RangeOptions { get; } = new[]
    {
        new LogRangeOption("Any time", null),
        new LogRangeOption("Last hour", TimeSpan.FromHours(1)),
        new LogRangeOption("Last 24 hours", TimeSpan.FromDays(1)),
        new LogRangeOption("Last 7 days", TimeSpan.FromDays(7)),
    };

    /// <summary>Worst first, as mobile's State chip.</summary>
    public static IReadOnlyList<LogStateOption> StateOptions { get; } = new[]
    {
        new LogStateOption(null, "Any state"),
        new LogStateOption(AlertState.Active, "Alerted"),
        new LogStateOption(AlertState.Worse, AlertState.Worse.ToDisplayString()),
        new LogStateOption(AlertState.Better, AlertState.Better.ToDisplayString()),
        new LogStateOption(AlertState.Acknowledged, AlertState.Acknowledged.ToDisplayString()),
        new LogStateOption(AlertState.Recovered, AlertState.Recovered.ToDisplayString()),
    };

    public ObservableCollection<FleetEventRow> Events { get; } = new();

    public ObservableCollection<FleetAlertRow> Alerts { get; } = new();

    public ObservableCollection<LogDeviceOption> DeviceOptions { get; } = new();

    /// <summary>"Any type", then every type in what's loaded.</summary>
    public ObservableCollection<string> TypeOptions { get; } = new();

    public CsvExport EventsCsv { get; }

    public CsvExport AlertsCsv { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand LoadMoreCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>A row double-clicked: that device's event log or alert history in Device Details.</summary>
    public RelayCommand OpenSelectedCommand { get; }

    public RelayCommand ShowEventLogCommand { get; }

    public RelayCommand ShowAlertLogCommand { get; }

    public bool ShowingEventLog
    {
        get => _showingEventLog;
        set
        {
            if (SetProperty(ref _showingEventLog, value))
            {
                OnPropertyChanged(nameof(ShowingAlertLog));
                OnPropertyChanged(nameof(CanLoadMore));
                OnPropertyChanged(nameof(EmptyText));
                OnPropertyChanged(nameof(ShowsEmpty));
                OnPropertyChanged(nameof(CountText));
                LoadMoreCommand.RaiseCanExecuteChanged();
                if (!(value ? _eventsLoaded : _alertsLoaded))
                {
                    _ = LoadAsync(reset: false);
                }
            }
        }
    }

    public bool ShowingAlertLog
    {
        get => !_showingEventLog;
        set => ShowingEventLog = !value;
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                FiltersChanged(refetchEvents: true);
            }
        }
    }

    public LogDeviceOption SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is not null && SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasActiveFilters));
                ClearFiltersCommand.RaiseCanExecuteChanged();
                _ = LoadAsync(reset: true);
            }
        }
    }

    public string SelectedType
    {
        get => _selectedType;
        set
        {
            if (value is not null && SetProperty(ref _selectedType, value))
            {
                FiltersChanged(refetchEvents: true);
            }
        }
    }

    public LogStateOption SelectedState
    {
        get => _selectedState;
        set
        {
            if (value is not null && SetProperty(ref _selectedState, value))
            {
                FiltersChanged(refetchEvents: false);
            }
        }
    }

    public LogRangeOption SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is not null && SetProperty(ref _selectedRange, value))
            {
                FiltersChanged(refetchEvents: false);
            }
        }
    }

    public FleetEventRow? SelectedEvent
    {
        get => _selectedEvent;
        set => SetProperty(ref _selectedEvent, value);
    }

    public FleetAlertRow? SelectedAlert
    {
        get => _selectedAlert;
        set => SetProperty(ref _selectedAlert, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                LoadMoreCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanLoadMore));
                OnPropertyChanged(nameof(ShowsEmpty));
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

    public bool HasError => _errorMessage is not null;

    public bool HasActiveFilters =>
        _searchText.Length > 0 || _selectedDevice.DeviceId is not null || _selectedType != AnyType
        || _selectedState.State is not null || _selectedRange.Span is not null;

    public bool CanLoadMore => !IsLoading && (ShowingEventLog
        ? _eventsLoaded && !_eventsExhausted && _eventLimit < MaxEntries
        : _alertsLoaded && !_alertsExhausted && _alertLimit < MaxEntries);

    public bool ShowsEmpty => !IsLoading && !HasError && (ShowingEventLog ? Events.Count == 0 && _eventsLoaded : Alerts.Count == 0 && _alertsLoaded);

    public string EmptyText => HasActiveFilters
        ? "Nothing matches these filters."
        : ShowingEventLog ? "No events logged." : "No alerts logged.";

    /// <summary>"Showing 100 events", for the footer.</summary>
    public string CountText => ShowingEventLog
        ? $"{Events.Count} event{(Events.Count == 1 ? string.Empty : "s")}"
        : $"{Alerts.Count} alert log entr{(Alerts.Count == 1 ? "y" : "ies")}";

    /// <summary>The tab has been shown: load what's showing, once.</summary>
    public void Activate()
    {
        if (!(ShowingEventLog ? _eventsLoaded : _alertsLoaded))
        {
            _ = LoadAsync(reset: false);
        }
    }

    /// <summary>The device list changed: refresh the Device filter, keeping the choice.</summary>
    public void SetDevices(IReadOnlyList<Device> devices)
    {
        var chosen = _selectedDevice.DeviceId;
        var options = devices
            .Select(d => new LogDeviceOption(d.DeviceId, d.BestName))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DeviceOptions.Clear();
        DeviceOptions.Add(AllDevices);
        foreach (var option in options)
        {
            DeviceOptions.Add(option);
        }

        _selectedDevice = DeviceOptions.FirstOrDefault(o => o.DeviceId == chosen) ?? AllDevices;
        OnPropertyChanged(nameof(SelectedDevice));
    }

    private void FiltersChanged(bool refetchEvents)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(EmptyText));
        ClearFiltersCommand.RaiseCanExecuteChanged();

        // A type or a search needs a deeper page to find anything rare.
        if (refetchEvents && ShowingEventLog && _eventsLoaded
            && EventLogFeed.FetchLimit(_eventLimit, TypeFilter, SearchFilter) > _events.Count && !_eventsExhausted)
        {
            _ = LoadAsync(reset: false);
            return;
        }

        Rebuild();
    }

    private void ClearFilters()
    {
        _searchText = string.Empty;
        _selectedType = AnyType;
        _selectedState = StateOptions[0];
        _selectedRange = RangeOptions[0];
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedType));
        OnPropertyChanged(nameof(SelectedState));
        OnPropertyChanged(nameof(SelectedRange));

        if (_selectedDevice.DeviceId is not null)
        {
            SelectedDevice = AllDevices;
        }
        else
        {
            FiltersChanged(refetchEvents: false);
        }
    }

    private string? TypeFilter => _selectedType == AnyType ? null : _selectedType;

    private string? SearchFilter => string.IsNullOrWhiteSpace(_searchText) ? null : _searchText.Trim();

    private async Task LoadMoreAsync()
    {
        if (ShowingEventLog)
        {
            _eventLimit = Math.Min(MaxEntries, _eventLimit + PageSize);
        }
        else
        {
            _alertLimit = Math.Min(MaxEntries, _alertLimit + PageSize);
        }

        await LoadAsync(reset: false).ConfigureAwait(true);
    }

    /// <param name="reset">The device changed: start again from one page, for both logs.</param>
    private async Task LoadAsync(bool reset)
    {
        if (reset)
        {
            _eventLimit = _alertLimit = PageSize;
            _eventsLoaded = _alertsLoaded = false;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var token = cts.Token;
        var events = ShowingEventLog;
        var deviceId = _selectedDevice.DeviceId;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            if (events)
            {
                var limit = EventLogFeed.FetchLimit(_eventLimit, TypeFilter, SearchFilter);
                var entries = await _client.Logs.ListEventLogAsync(deviceId, limit, token).ConfigureAwait(true);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _events = entries;
                _eventsExhausted = entries.Count < limit;
                _eventsLoaded = true;
                RefreshTypes();
            }
            else
            {
                if (_rules.Count == 0)
                {
                    _rules = await LoadRulesAsync(token).ConfigureAwait(true);
                }

                var entries = await _client.Logs.ListAlertLogAsync(deviceId, _alertLimit, token).ConfigureAwait(true);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _alerts = entries;
                _alertsExhausted = entries.Count < _alertLimit;
                _alertsLoaded = true;
            }

            Rebuild();
        }
        catch (OperationCanceledException)
        {
            // A newer load replaced this one.
        }
        catch (LibreNmsApiException ex) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not load the {Log} for the Logs tab", events ? "event log" : "alert log");
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Rule names and severities, best effort - a token that can't read rules gets "Rule 12".</summary>
    private async Task<IReadOnlyDictionary<int, AlertRule>> LoadRulesAsync(CancellationToken token)
    {
        try
        {
            var rules = await _client.Rules.ListAsync(token).ConfigureAwait(true);
            return rules.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogDebug(ex, "Could not list alert rules for the alert log");
            return new Dictionary<int, AlertRule>();
        }
    }

    private void RefreshTypes()
    {
        var chosen = _selectedType;
        TypeOptions.Clear();
        TypeOptions.Add(AnyType);
        foreach (var type in EventLogFeed.Types(_events))
        {
            TypeOptions.Add(type);
        }

        if (chosen != AnyType && !TypeOptions.Contains(chosen))
        {
            TypeOptions.Add(chosen);
        }

        _selectedType = chosen;
        OnPropertyChanged(nameof(SelectedType));
    }

    /// <summary>Applies the filters that narrow what's loaded.</summary>
    private void Rebuild()
    {
        var utc = _settings.Current.ServerTimestampsAreUtc;
        var since = _selectedRange.Span is { } span ? DateTime.Now - span : (DateTime?)null;

        Events.Clear();
        var events = EventLogFeed.Filter(_events, TypeFilter, SearchFilter, _eventLimit);
        foreach (var entry in events)
        {
            var local = ServerTime.ToLocal(entry.Timestamp, utc);
            if (since is { } from && (local is null || local < from))
            {
                continue;
            }

            Events.Add(new FleetEventRow(entry, NameOf(entry.DeviceId, entry.SysName, entry.Hostname), local));
        }

        Alerts.Clear();
        var search = SearchFilter;
        foreach (var entry in _alerts)
        {
            if (_selectedState.State is { } state && entry.State != state)
            {
                continue;
            }

            var local = ServerTime.ToLocal(entry.TimeLogged, utc);
            if (since is { } from && (local is null || local < from))
            {
                continue;
            }

            var row = new FleetAlertRow(entry, NameOf(entry.DeviceId, entry.SysName, entry.Hostname), local, _rules.GetValueOrDefault(entry.RuleId));
            if (search is not null
                && !row.DeviceName.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !row.RuleName.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !row.DetailText.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Alerts.Add(row);
        }

        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CanLoadMore));
        LoadMoreCommand.RaiseCanExecuteChanged();
    }

    private string NameOf(int deviceId, string? sysName, string? hostname) =>
        _devices.Get(deviceId)?.BestName
        ?? (string.IsNullOrWhiteSpace(sysName) ? null : sysName)
        ?? (string.IsNullOrWhiteSpace(hostname) ? $"device {deviceId}" : hostname!);

    private void OpenSelected()
    {
        if (ShowingEventLog && SelectedEvent is { } ev)
        {
            _windows.ShowDeviceEventLog(ev.Entry.DeviceId);
        }
        else if (ShowingAlertLog && SelectedAlert is { } alert)
        {
            _windows.ShowDeviceAlerts(alert.Entry.DeviceId);
        }
    }
}

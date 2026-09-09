using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>View model behind the main alert window.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly AlertMonitor _monitor;
    private readonly IDeviceCache _devices;
    private readonly IAlertRuleCache _rules;
    private readonly IWindowService _windows;
    private readonly DeviceListViewModel _deviceList;
    private readonly HealthViewModel _health;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, AlertItemViewModel> _index = new();
    private readonly DispatcherTimer _ageTimer;

    private AlertItemViewModel? _selectedAlert;
    private readonly List<AlertItemViewModel> _selectedAlerts = new();
    private string _statusMessage = "Starting...";
    private string? _errorMessage;
    private bool _isBusy;
    private bool _isConnected;
    private bool _isBulkUpdating;
    private DateTimeOffset? _lastUpdated;
    private string _searchText = string.Empty;
    private string _acknowledgeNote = string.Empty;
    private bool _suppressFilterPersistence;
    private bool _showAllFaultFields;
    private MainTab _selectedTab = MainTab.Alerts;

    /// <summary>Cancels the in-flight fault lookup when the selection moves on.</summary>
    private CancellationTokenSource? _detailCts;

    public MainViewModel(
        ILibreNmsClient client,
        ISessionService session,
        ISettingsStore settings,
        AlertMonitor monitor,
        IDeviceCache devices,
        IAlertRuleCache rules,
        IWindowService windows,
        DeviceListViewModel deviceList,
        HealthViewModel health,
        ILogger<MainViewModel> logger)
    {
        _client = client;
        _session = session;
        _settings = settings;
        _monitor = monitor;
        _devices = devices;
        _rules = rules;
        _windows = windows;
        _deviceList = deviceList;
        _health = health;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Alerts = new ObservableCollection<AlertItemViewModel>();
        AlertsView = CollectionViewSource.GetDefaultView(Alerts);
        AlertsView.Filter = FilterAlert;

        RefreshCommand = new RelayCommand(RequestRefresh, () => _isConnected && !_isBusy);
        AcknowledgeCommand = new AsyncRelayCommand(AcknowledgeSelectedAsync, () => CanAcknowledge);
        UnacknowledgeCommand = new AsyncRelayCommand(UnacknowledgeSelectedAsync, () => CanUnacknowledge);
        OpenAlertCommand = new RelayCommand(OpenSelectedAlert, () => SelectedAlert?.AlertUrl is not null);
        OpenDeviceCommand = new RelayCommand(OpenSelectedDevice, () => SelectedAlert?.DeviceUrl is not null);
        OpenProcedureCommand = new RelayCommand(OpenSelectedProcedure, () => SelectedAlert?.HasProcedure == true);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ReloadDetailCommand = new RelayCommand(() => ReloadDetail(force: true), () => SelectedAlert is not null);
        SelectDashboardTabCommand = new RelayCommand(() => SelectedTab = MainTab.Dashboard);
        SelectDevicesTabCommand = new RelayCommand(() => SelectedTab = MainTab.Devices);
        SelectHealthTabCommand = new RelayCommand(() => SelectedTab = MainTab.Health);
        SelectAlertsTabCommand = new RelayCommand(() => SelectedTab = MainTab.Alerts);
        RefreshCurrentTabCommand = new RelayCommand(RefreshCurrentTab);
        ClearCurrentTabFiltersCommand = new RelayCommand(ClearCurrentTabFilters);
        SettingsCommand = new RelayCommand(OpenSettings);
        SignOutCommand = new RelayCommand(SignOut, () => _isConnected);
        ExitCommand = new RelayCommand(() => _windows.Exit());

        LoadFilterFromSettings();

        // Set directly rather than through the SelectedTab property: the
        // property's OnShown side effect would be a no-op here anyway because
        // the session has not been restored yet (see OnConnected, which
        // re-triggers OnShown once a connection actually exists).
        _selectedTab = MapStartupTab(_settings.Current.StartupTab);

        _monitor.Polled += OnPolled;
        _monitor.PollStarted += OnPollStarted;
        _session.StateChanged += OnSessionStateChanged;

        // Ages are relative, so they have to be nudged even when nothing polls.
        _ageTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _ageTimer.Tick += (_, _) => RefreshAges();
        _ageTimer.Start();

        UpdateConnectionState();
    }

    public ObservableCollection<AlertItemViewModel> Alerts { get; }

    public ICollectionView AlertsView { get; }

    public RelayCommand RefreshCommand { get; }

    public AsyncRelayCommand AcknowledgeCommand { get; }

    public AsyncRelayCommand UnacknowledgeCommand { get; }

    public RelayCommand OpenAlertCommand { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand OpenProcedureCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>Re-fetches the faults for the selected alert.</summary>
    public RelayCommand ReloadDetailCommand { get; }

    public RelayCommand SelectDashboardTabCommand { get; }

    public RelayCommand SelectDevicesTabCommand { get; }

    public RelayCommand SelectHealthTabCommand { get; }

    public RelayCommand SelectAlertsTabCommand { get; }

    /// <summary>F5: refreshes whichever tab is currently showing.</summary>
    public RelayCommand RefreshCurrentTabCommand { get; }

    /// <summary>Ctrl+L: clears the filters on whichever tab is currently showing.</summary>
    public RelayCommand ClearCurrentTabFiltersCommand { get; }

    public RelayCommand SettingsCommand { get; }

    public RelayCommand SignOutCommand { get; }

    public RelayCommand ExitCommand { get; }

    /// <summary>The device list, for the Devices tab's content to bind to.</summary>
    public DeviceListViewModel DeviceList => _deviceList;

    /// <summary>The sensor health list, for the Health tab's content to bind to.</summary>
    public HealthViewModel Health => _health;

    public MainTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsDashboardTabSelected));
                OnPropertyChanged(nameof(IsDevicesTabSelected));
                OnPropertyChanged(nameof(IsHealthTabSelected));
                OnPropertyChanged(nameof(IsAlertsTabSelected));

                // Loaded once, lazily, the first time a tab is actually looked at.
                if (value == MainTab.Devices)
                {
                    _deviceList.OnShown();
                }
                else if (value == MainTab.Health)
                {
                    _health.OnShown();
                }
            }
        }
    }

    public bool IsDashboardTabSelected => SelectedTab == MainTab.Dashboard;

    public bool IsDevicesTabSelected => SelectedTab == MainTab.Devices;

    public bool IsHealthTabSelected => SelectedTab == MainTab.Health;

    public bool IsAlertsTabSelected => SelectedTab == MainTab.Alerts;

    // -------------------------------------------------------------- filtering

    private bool _showCritical = true;
    private bool _showWarning = true;
    private bool _showUnknownSeverity = true;
    private bool _showAcknowledged = true;

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

    public bool ShowUnknownSeverity
    {
        get => _showUnknownSeverity;
        set { if (SetProperty(ref _showUnknownSeverity, value)) OnFilterChanged(); }
    }

    /// <summary>
    /// Active alerts always show; this adds acknowledged ones alongside them.
    /// Recovered alerts never show here regardless - there is no chip for them.
    /// </summary>
    public bool ShowAcknowledged
    {
        get => _showAcknowledged;
        set { if (SetProperty(ref _showAcknowledged, value)) OnFilterChanged(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) OnFilterChanged(); }
    }

    // ------------------------------------------------------------------ state

    public AlertItemViewModel? SelectedAlert
    {
        get => _selectedAlert;
        set
        {
            if (SetProperty(ref _selectedAlert, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanAcknowledge));
                OnPropertyChanged(nameof(CanUnacknowledge));
                RaiseCommandStates();
                ReloadDetail(force: false);
            }
        }
    }

    public bool HasSelection => SelectedAlert is not null;

    /// <summary>
    /// Every row currently highlighted in the grid (Ctrl/Shift-click), kept in
    /// sync by the view's SelectionChanged handler since DataGrid.SelectedItems
    /// is not a bindable dependency property. Always contains
    /// <see cref="SelectedAlert"/> when exactly one row is selected.
    /// </summary>
    public IReadOnlyList<AlertItemViewModel> SelectedAlerts => _selectedAlerts;

    public int SelectedCount => _selectedAlerts.Count;

    public bool HasMultipleSelection => SelectedCount > 1;

    /// <summary>True when exactly one alert is selected, i.e. the single-alert detail view should show.</summary>
    public bool IsSingleSelection => SelectedCount == 1;

    public string AcknowledgeButtonLabel => HasMultipleSelection ? $"Acknowledge {SelectedCount}" : "Acknowledge";

    public string UnacknowledgeButtonLabel => HasMultipleSelection ? $"Return {SelectedCount} to active" : "Return to active";

    /// <summary>A short "3 critical, 2 warning" breakdown of the current multi-selection.</summary>
    public string SelectionSummary
    {
        get
        {
            var critical = _selectedAlerts.Count(a => a.Severity == AlertSeverity.Critical);
            var warning = _selectedAlerts.Count(a => a.Severity == AlertSeverity.Warning);
            var other = SelectedCount - critical - warning;

            var parts = new List<string>();
            if (critical > 0) parts.Add($"{critical} critical");
            if (warning > 0) parts.Add($"{warning} warning");
            if (other > 0) parts.Add($"{other} other");

            return parts.Count > 0 ? string.Join(", ", parts) : string.Empty;
        }
    }

    /// <summary>
    /// Called by the view whenever the grid's multi-selection changes. A plain
    /// method rather than a bindable collection, since DataGrid.SelectedItems
    /// has no dependency-property binding path.
    /// </summary>
    public void UpdateSelectedAlerts(IEnumerable<AlertItemViewModel> alerts)
    {
        _selectedAlerts.Clear();
        _selectedAlerts.AddRange(alerts);

        OnPropertyChanged(nameof(SelectedAlerts));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(IsSingleSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(AcknowledgeButtonLabel));
        OnPropertyChanged(nameof(UnacknowledgeButtonLabel));
        OnPropertyChanged(nameof(CanAcknowledge));
        OnPropertyChanged(nameof(CanUnacknowledge));
        RaiseCommandStates();
    }

    public bool CanAcknowledge =>
        _isConnected && !_isBulkUpdating && SelectedAlerts.Count > 0
        && (SelectedAlerts.Count > 1 || SelectedAlerts[0].State != AlertState.Acknowledged);

    public bool CanUnacknowledge =>
        _isConnected && !_isBulkUpdating && SelectedAlerts.Count > 0
        && (SelectedAlerts.Count > 1 || SelectedAlerts[0].State == AlertState.Acknowledged);

    public string AcknowledgeNote
    {
        get => _acknowledgeNote;
        set => SetProperty(ref _acknowledgeNote, value);
    }

    /// <summary>
    /// Expands each fault card from the columns the rule tests to the whole
    /// matched row. Session-only: it is a "let me dig in" gesture, not a setting.
    /// </summary>
    public bool ShowAllFaultFields
    {
        get => _showAllFaultFields;
        set => SetProperty(ref _showAllFaultFields, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(CanAcknowledge));
                OnPropertyChanged(nameof(CanUnacknowledge));
                RaiseCommandStates();
            }
        }
    }

    public string ServerDescription
    {
        get
        {
            var connection = _session.Connection;
            if (connection is null)
            {
                return "Not connected";
            }

            var version = _session.ServerInfo?.LocalVersion;
            return version is null
                ? connection.WebRoot.Host
                : $"{connection.WebRoot.Host} - LibreNMS {version}";
        }
    }

    public string LastUpdatedText => _lastUpdated is null
        ? "never"
        : _lastUpdated.Value.LocalDateTime.ToString("HH:mm:ss");

    // ----------------------------------------------------------------- counts

    public int CriticalCount => Alerts.Count(a => a.Severity == AlertSeverity.Critical && a.State == AlertState.Active);

    public int WarningCount => Alerts.Count(a => a.Severity == AlertSeverity.Warning && a.State == AlertState.Active);

    public int AcknowledgedCount => Alerts.Count(a => a.State == AlertState.Acknowledged);

    public int TotalCount => Alerts.Count;

    public int VisibleCount => AlertsView.Cast<object>().Count();

    // --------------------------------------------------------------- lifetime

    /// <summary>Called by the app once the session is established.</summary>
    public void OnConnected()
    {
        UpdateConnectionState();
        StatusMessage = "Connected. Waiting for the first poll...";
        RequestRefresh();

        // The initial tab may have been set (from StartupTab) before the
        // session was restored, when OnShown would have found nothing to load.
        if (SelectedTab == MainTab.Devices)
        {
            _deviceList.OnShown();
        }
        else if (SelectedTab == MainTab.Health)
        {
            _health.OnShown();
        }
    }

    private static MainTab MapStartupTab(StartupTab tab) => tab switch
    {
        StartupTab.Devices => MainTab.Devices,
        StartupTab.Health => MainTab.Health,
        StartupTab.Alerts => MainTab.Alerts,
        _ => MainTab.Dashboard,
    };

    public void RequestRefresh()
    {
        if (!_session.IsConnected)
        {
            StatusMessage = "Not connected.";
            return;
        }

        IsBusy = true;
        _monitor.RequestRefresh();
    }

    /// <summary>
    /// Clears the severity/state filters and searches for the given device, so
    /// every active or acknowledged alert against it is visible. Used by the
    /// device view's "Show alerts" action.
    /// </summary>
    public void ShowAlertsForDevice(string deviceSearchTerm)
    {
        SelectedTab = MainTab.Alerts;
        _suppressFilterPersistence = true;

        ShowCritical = true;
        ShowWarning = true;
        ShowUnknownSeverity = true;
        ShowAcknowledged = true;
        SearchText = deviceSearchTerm;

        _suppressFilterPersistence = false;
        OnFilterChanged();
    }

    /// <summary>Selects the given alert, bringing it into view. Used by toast activation.</summary>
    public void SelectAlert(int alertId)
    {
        if (_index.TryGetValue(alertId, out var item))
        {
            // A filter chip may be hiding the alert the user just clicked on.
            if (!FilterAlert(item))
            {
                ClearFilters();
            }

            SelectedAlert = item;
        }
    }

    /// <summary>
    /// Acknowledges an alert by id, regardless of what is currently selected
    /// in the grid. Used by the toast button, which acts on whichever alert
    /// the toast was about.
    /// </summary>
    public async Task AcknowledgeAsync(int alertId)
    {
        if (!_index.TryGetValue(alertId, out var item) || !_session.IsConnected)
        {
            return;
        }

        item.IsUpdating = true;

        try
        {
            await _client.Alerts.AcknowledgeAsync(item.Id, "Acknowledged from DashyNMS", untilClear: true).ConfigureAwait(true);
            StatusMessage = $"Acknowledged alert #{item.Id}.";
            _logger.LogInformation("Acknowledged alert {AlertId}", item.Id);
            RequestRefresh();
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not acknowledge alert {AlertId}", item.Id);
            _windows.ShowError("Acknowledge failed", ex.ToUserMessage());
        }
        finally
        {
            item.IsUpdating = false;
        }
    }

    // ---------------------------------------------------------------- polling

    private void OnPollStarted(object? sender, EventArgs e)
        => _dispatcher.InvokeAsync(() => IsBusy = true);

    private void OnPolled(object? sender, AlertPollResult result)
        => _dispatcher.InvokeAsync(() => ApplyPollResult(result));

    private void ApplyPollResult(AlertPollResult result)
    {
        IsBusy = false;

        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            StatusMessage = "Last refresh failed.";

            if (result.IsAuthenticationFailure)
            {
                StatusMessage = "The API token was rejected. Sign in again.";
            }

            return;
        }

        ErrorMessage = null;
        _lastUpdated = result.CompletedAt;

        ApplyAlerts(result.Alerts);

        StatusMessage = TotalCount == 0
            ? "No alerts."
            : $"{CriticalCount} critical, {WarningCount} warning, {AcknowledgedCount} acknowledged.";

        OnPropertyChanged(nameof(LastUpdatedText));
    }

    private void ApplyAlerts(IReadOnlyList<Alert> alerts)
    {
        var context = CreateDisplayContext();
        var incoming = alerts.Select(a => a.Id).ToHashSet();

        // Drop anything the server no longer reports.
        for (var i = Alerts.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Alerts[i].Id))
            {
                _index.Remove(Alerts[i].Id);
                Alerts.RemoveAt(i);
            }
        }

        // Align the collection with the server's ordering, updating in place so
        // the selection and scroll position survive a refresh.
        for (var target = 0; target < alerts.Count; target++)
        {
            var alert = alerts[target];

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

        RaiseCountsChanged();

        // The selected row may have been removed by this refresh.
        if (SelectedAlert is not null && !_index.ContainsKey(SelectedAlert.Id))
        {
            SelectedAlert = null;
        }
        else
        {
            OnPropertyChanged(nameof(CanAcknowledge));
            OnPropertyChanged(nameof(CanUnacknowledge));
            RaiseCommandStates();

            // The alert may have moved on since the faults were fetched.
            ReloadDetail(force: false);
        }
    }

    // ------------------------------------------------------- triggering faults

    private AlertDisplayContext CreateDisplayContext()
        => AlertDisplayContext.Create(_settings.Current, _session.Connection, _devices);

    /// <summary>Re-renders every row after a display setting changed.</summary>
    private void ReapplyDisplayContext()
    {
        var context = CreateDisplayContext();

        foreach (var alert in Alerts)
        {
            alert.ApplyContext(context);
        }
    }

    /// <summary>
    /// Fetches the rows the rule matched for the selected alert. LibreNMS keeps
    /// these on the alert log rather than the alert itself, so it is a separate
    /// request and is only made for the alert the user is actually looking at.
    /// </summary>
    private void ReloadDetail(bool force)
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        ReloadDetailCommand.RaiseCanExecuteChanged();

        if (SelectedAlert is not { } item)
        {
            return;
        }

        if (!_settings.Current.LoadAlertFaults || !_session.IsConnected)
        {
            return;
        }

        if (!force && !item.NeedsDetail)
        {
            return;
        }

        _detailCts = new CancellationTokenSource();
        _ = LoadDetailAsync(item, _detailCts.Token);
    }

    private async Task LoadDetailAsync(AlertItemViewModel item, CancellationToken cancellationToken)
    {
        item.BeginLoadingDetail();

        try
        {
            // The rule tells us which columns matter; the alert log tells us
            // what their values were. Both are needed to make sense of a fault.
            var conditionFields = await _rules
                .GetConditionFieldsAsync(item.RuleId, cancellationToken)
                .ConfigureAwait(true);

            var entry = await _client.Logs
                .GetLatestForRuleAsync(item.DeviceId, item.RuleId, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            item.SetDetail(AlertFaultParser.Parse(entry, conditionFields));
        }
        catch (OperationCanceledException)
        {
            // The user selected a different alert; nothing to report.
        }
        catch (LibreNmsApiException ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not load faults for alert {AlertId}", item.Id);
                item.SetDetailFailed(ex.ToUserMessage());
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not load faults for alert {AlertId}", item.Id);
                item.SetDetailFailed(ex.Message);
            }
        }
    }

    // --------------------------------------------------------------- commands

    private Task AcknowledgeSelectedAsync()
        => RunBulkAsync(
            "Acknowledge failed",
            item => _client.Alerts.AcknowledgeAsync(
                item.Id,
                string.IsNullOrWhiteSpace(AcknowledgeNote) ? "Acknowledged from DashyNMS" : AcknowledgeNote.Trim(),
                untilClear: true),
            successCountVerb: "Acknowledged",
            onAllSucceeded: () => AcknowledgeNote = string.Empty);

    private Task UnacknowledgeSelectedAsync()
        => RunBulkAsync(
            "Could not unacknowledge",
            item => _client.Alerts.UnmuteAsync(item.Id, "Unacknowledged from DashyNMS"),
            successCountVerb: "Returned to active");

    /// <summary>
    /// Applies <paramref name="action"/> to every selected alert concurrently.
    /// One alert failing does not stop the others; failures are reported
    /// together once everything has finished.
    /// </summary>
    private async Task RunBulkAsync(
        string failureTitle,
        Func<AlertItemViewModel, Task> action,
        string successCountVerb,
        Action? onAllSucceeded = null)
    {
        if (!_session.IsConnected || SelectedAlerts.Count == 0)
        {
            return;
        }

        // Snapshot: RequestRefresh() at the end can reshuffle the live
        // collection, and the grid selection itself may change underneath us.
        var items = SelectedAlerts.ToList();

        _isBulkUpdating = true;
        foreach (var item in items)
        {
            item.IsUpdating = true;
        }

        OnPropertyChanged(nameof(CanAcknowledge));
        OnPropertyChanged(nameof(CanUnacknowledge));
        RaiseCommandStates();

        var failures = new List<(AlertItemViewModel Item, string Message)>();

        await Task.WhenAll(items.Select(async item =>
        {
            try
            {
                await action(item).ConfigureAwait(true);
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogWarning(ex, "Bulk action failed for alert {AlertId}", item.Id);
                lock (failures)
                {
                    failures.Add((item, ex.ToUserMessage()));
                }
            }
        })).ConfigureAwait(true);

        foreach (var item in items)
        {
            item.IsUpdating = false;
        }

        _isBulkUpdating = false;
        OnPropertyChanged(nameof(CanAcknowledge));
        OnPropertyChanged(nameof(CanUnacknowledge));
        RaiseCommandStates();

        var succeeded = items.Count - failures.Count;

        if (failures.Count == 0)
        {
            StatusMessage = items.Count == 1
                ? $"{successCountVerb} alert #{items[0].Id}."
                : $"{successCountVerb} {succeeded} alerts.";
            _logger.LogInformation("{Verb} {Count} alert(s): {Ids}", successCountVerb, succeeded, string.Join(", ", items.Select(i => i.Id)));
            onAllSucceeded?.Invoke();
        }
        else if (succeeded == 0)
        {
            StatusMessage = "Last action failed.";
            _windows.ShowError(failureTitle, failures[0].Message);
        }
        else
        {
            StatusMessage = $"{successCountVerb} {succeeded} of {items.Count} alerts; {failures.Count} failed.";
            _windows.ShowError(
                failureTitle,
                $"{succeeded} of {items.Count} succeeded. First failure (alert #{failures[0].Item.Id}): {failures[0].Message}");
        }

        RequestRefresh();
    }

    private void OpenSelectedAlert()
    {
        if (SelectedAlert?.AlertUrl is { } url)
        {
            _windows.OpenUrl(url);
        }
    }

    private void OpenSelectedDevice()
    {
        if (SelectedAlert?.DeviceUrl is { } url)
        {
            _windows.OpenUrl(url);
        }
    }

    private void OpenSelectedProcedure()
    {
        if (SelectedAlert?.ProcedureUrl is { } raw && Uri.TryCreate(raw, UriKind.Absolute, out var url))
        {
            _windows.OpenUrl(url);
        }
    }

    private void RefreshCurrentTab()
    {
        switch (SelectedTab)
        {
            case MainTab.Devices:
                if (_deviceList.RefreshCommand.CanExecute(null))
                {
                    _deviceList.RefreshCommand.Execute(null);
                }

                break;

            case MainTab.Health:
                if (_health.RefreshCommand.CanExecute(null))
                {
                    _health.RefreshCommand.Execute(null);
                }

                break;

            case MainTab.Alerts:
                RequestRefresh();
                break;

            case MainTab.Dashboard:
            default:
                break;
        }
    }

    private void ClearCurrentTabFilters()
    {
        switch (SelectedTab)
        {
            case MainTab.Devices:
                _deviceList.ClearFiltersCommand.Execute(null);
                break;

            case MainTab.Health:
                _health.ClearFiltersCommand.Execute(null);
                break;

            case MainTab.Alerts:
                ClearFilters();
                break;

            case MainTab.Dashboard:
            default:
                break;
        }
    }

    private void ClearFilters()
    {
        _suppressFilterPersistence = true;

        ShowCritical = true;
        ShowWarning = true;
        ShowUnknownSeverity = true;
        ShowAcknowledged = true;
        SearchText = string.Empty;

        _suppressFilterPersistence = false;
        OnFilterChanged();
    }

    private void OpenSettings()
    {
        if (!_windows.ShowSettingsDialog())
        {
            return;
        }

        OnPropertyChanged(nameof(ServerDescription));

        // The device name style may have changed, and the device list is what
        // backs it, so pull both through before refreshing.
        _devices.Invalidate();
        ReapplyDisplayContext();
        ReloadDetail(force: true);
        RequestRefresh();
    }

    private void SignOut()
    {
        if (!_windows.Confirm("Sign out", "Sign out and forget the stored API token?"))
        {
            return;
        }

        _session.SignOut(forgetToken: true);

        // The next server may reuse alert ids and rule ids, so the memory from
        // the old one is worse than useless.
        _monitor.ResetHistory();
        _rules.Clear();
        _devices.Invalidate();

        Alerts.Clear();
        _index.Clear();
        SelectedAlert = null;
        RaiseCountsChanged();

        StatusMessage = "Signed out.";

        if (_windows.ShowSignInDialog())
        {
            OnConnected();
        }
    }

    // ---------------------------------------------------------------- helpers

    private bool FilterAlert(object item)
    {
        if (item is not AlertItemViewModel alert)
        {
            return false;
        }

        var severityAllowed = alert.Severity switch
        {
            AlertSeverity.Critical => ShowCritical,
            AlertSeverity.Warning => ShowWarning,
            // Ok and Unknown severities have no chip of their own - both are
            // rare enough that they always show, gated only by this one
            // hidden flag (which is always true) rather than a dedicated one each.
            _ => ShowUnknownSeverity,
        };

        if (!severityAllowed)
        {
            return false;
        }

        var stateAllowed = alert.State switch
        {
            AlertState.Acknowledged => ShowAcknowledged,
            // Recovered alerts are never shown in the quick filter; Active has
            // no chip of its own since it is always the baseline.
            AlertState.Recovered => false,
            _ => true,
        };

        if (!stateAllowed)
        {
            return false;
        }

        var term = SearchText;
        return string.IsNullOrWhiteSpace(term) || alert.Matches(term.Trim());
    }

    private void OnFilterChanged()
    {
        AlertsView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));

        if (!_suppressFilterPersistence)
        {
            SaveFilterToSettings();
        }
    }

    private void LoadFilterFromSettings()
    {
        var filter = _settings.Current.Filter;

        _suppressFilterPersistence = true;

        _showCritical = filter.ShowCritical;
        _showWarning = filter.ShowWarning;
        _showUnknownSeverity = filter.ShowUnknownSeverity;
        _showAcknowledged = filter.ShowAcknowledged;
        _searchText = filter.SearchText ?? string.Empty;

        _suppressFilterPersistence = false;
    }

    private void SaveFilterToSettings()
    {
        var filter = _settings.Current.Filter;

        filter.ShowCritical = ShowCritical;
        filter.ShowWarning = ShowWarning;
        filter.ShowUnknownSeverity = ShowUnknownSeverity;
        filter.ShowAcknowledged = ShowAcknowledged;
        filter.SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;

        _settings.Save();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
        => _dispatcher.InvokeAsync(UpdateConnectionState);

    private void UpdateConnectionState()
    {
        IsConnected = _session.IsConnected;
        OnPropertyChanged(nameof(ServerDescription));

        if (!IsConnected)
        {
            StatusMessage = "Not connected.";
        }
    }

    private void RefreshAges()
    {
        foreach (var alert in Alerts)
        {
            alert.RefreshAge();
        }
    }

    private void RaiseCountsChanged()
    {
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(AcknowledgedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        AcknowledgeCommand.RaiseCanExecuteChanged();
        UnacknowledgeCommand.RaiseCanExecuteChanged();
        OpenAlertCommand.RaiseCanExecuteChanged();
        OpenDeviceCommand.RaiseCanExecuteChanged();
        OpenProcedureCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _ageTimer.Stop();
        _monitor.Polled -= OnPolled;
        _monitor.PollStarted -= OnPollStarted;
        _session.StateChanged -= OnSessionStateChanged;

        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;
    }
}

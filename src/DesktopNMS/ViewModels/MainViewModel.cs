using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DesktopNMS.ViewModels;

/// <summary>View model behind the main alert window.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Above this many alerts, a bulk acknowledge/unacknowledge asks for
    /// confirmation first rather than acting immediately - matches the
    /// "collapse into one summary" toast threshold's default, another place
    /// a handful is fine but more than that warrants a second look.
    /// </summary>
    private const int BulkConfirmThreshold = 5;

    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly AlertMonitor _monitor;
    private readonly IDeviceCache _devices;
    private readonly IAlertRuleCache _rules;
    private readonly IDeviceGroupMembershipService _groupMembership;
    private readonly IWindowService _windows;
    private readonly DeviceListViewModel _deviceList;
    private readonly HealthViewModel _health;
    private readonly DashboardViewModel _dashboard;
    private readonly GroupsViewModel _groups;
    private readonly LocationsViewModel _locations;
    private readonly RulesViewModel _rulesTab;
    private readonly TemplatesViewModel _templates;
    private readonly NetworkMapViewModel _networkMap;
    private readonly GeoMapViewModel _geoMap;
    private readonly CustomMapsViewModel _customMaps;
    private readonly LogsViewModel _logs;
    private readonly IGraylogApi _graylog;
    private readonly IServerBrandingService _branding;
    private readonly ISelfActionTracker _selfActions;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, AlertItemViewModel> _index = new();
    private readonly DispatcherTimer _ageTimer;
    private readonly AutoRefreshTimer _refreshCountdown;

    private AlertItemViewModel? _selectedAlert;
    private readonly List<AlertItemViewModel> _selectedAlerts = new();
    private string _statusMessage = "Starting...";
    private string? _errorMessage;
    private bool _isBusy;
    private bool _isConnected;
    private bool _isBulkUpdating;
    private DateTimeOffset? _lastUpdated;
    private string _searchText = string.Empty;
    private AlertRule? _filterRule;
    private int? _filterDeviceId;
    private string _filterDeviceName = string.Empty;
    private string _acknowledgeNote = string.Empty;
    private bool _suppressFilterPersistence;
    private bool _showAllFaultFields;
    private MainTab _selectedTab = MainTab.Alerts;

    /// <summary>Cancels the in-flight fault lookup when the selection moves on.</summary>
    private CancellationTokenSource? _detailCts;

    /// <summary>
    /// DashyNMS's own icon, decoded once and reused for every window rather
    /// than on every <see cref="HeaderLogo"/> access - it never changes, so
    /// there is nothing to gain by re-decoding it.
    /// </summary>
    private static readonly Lazy<BitmapImage> AppIconLogo = new(() =>
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri("pack://application:,,,/Assets/app.ico");
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    });

    public MainViewModel(
        ILibreNmsClient client,
        ISessionService session,
        ISettingsStore settings,
        AlertMonitor monitor,
        IDeviceCache devices,
        IAlertRuleCache rules,
        IDeviceGroupMembershipService groupMembership,
        IWindowService windows,
        DeviceListViewModel deviceList,
        HealthViewModel health,
        DashboardViewModel dashboard,
        GroupsViewModel groups,
        LocationsViewModel locations,
        RulesViewModel rulesTab,
        TemplatesViewModel templates,
        NetworkMapViewModel networkMap,
        GeoMapViewModel geoMap,
        CustomMapsViewModel customMaps,
        LogsViewModel logs,
        IGraylogApi graylog,
        IServerBrandingService branding,
        ISelfActionTracker selfActions,
        ILogger<MainViewModel> logger)
    {
        _client = client;
        _session = session;
        _settings = settings;
        _monitor = monitor;
        _devices = devices;
        _rules = rules;
        _groupMembership = groupMembership;
        _windows = windows;
        _deviceList = deviceList;
        _health = health;
        _dashboard = dashboard;
        _groups = groups;
        _locations = locations;
        _rulesTab = rulesTab;
        _templates = templates;
        _networkMap = networkMap;
        _geoMap = geoMap;
        _customMaps = customMaps;
        _logs = logs;
        _graylog = graylog;
        _graylog.ConfigurationChanged += OnGraylogConfigurationChanged;
        _branding = branding;
        _selfActions = selfActions;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _branding.Changed += OnBrandingChanged;
        _settings.Changed += OnLogoSettingChanged;

        Alerts = new ObservableCollection<AlertItemViewModel>();
        AlertsView = CollectionViewSource.GetDefaultView(Alerts);
        GroupFilter = new FilterFacet(OnFilterChanged);
        GroupFilter.PropertyChanged += OnGroupFilterPropertyChanged;
        _groupMembership.Changed += OnGroupMembershipChanged;

        AlertsView.Filter = FilterAlert;

        RefreshCommand = new RelayCommand(RequestRefresh, () => _isConnected && !_isBusy);
        AcknowledgeCommand = new AsyncRelayCommand(AcknowledgeSelectedAsync, () => CanAcknowledge);
        UnacknowledgeCommand = new AsyncRelayCommand(UnacknowledgeSelectedAsync, () => CanUnacknowledge);
        OpenDeviceCommand = new RelayCommand(OpenSelectedDevice, () => SelectedAlert is not null);
        // The row's procedure icon passes its own alert as the parameter
        // (clicking it needn't select the row first); the context menu and
        // detail pane pass nothing and act on the selection.
        OpenProcedureCommand = new RelayCommand(OpenProcedure, p => (p as AlertItemViewModel ?? SelectedAlert)?.HasProcedure == true);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ClearRuleFilterCommand = new RelayCommand(() => FilterRule = null);
        ClearDeviceFilterCommand = new RelayCommand(() => SetDeviceFilter(null, null));
        FilterBySelectedRuleCommand = new RelayCommand(FilterBySelectedRule, () => SelectedAlert is not null);
        FilterBySelectedDeviceCommand = new RelayCommand(FilterBySelectedDevice, () => SelectedAlert is not null);
        ShowAlertFiltersCommand = new RelayCommand(ShowAlertFilters);
        CopyAlertsCsvCommand = new RelayCommand(CopyAlertsCsv);
        ExportAlertsCsvCommand = new RelayCommand(ExportAlertsCsv);
        ReloadDetailCommand = new RelayCommand(() => ReloadDetail(force: true), () => SelectedAlert is not null);
        SelectDashboardTabCommand = new RelayCommand(() => SelectedTab = MainTab.Dashboard);
        SelectDevicesTabCommand = new RelayCommand(() => SelectedTab = MainTab.Devices);
        SelectHealthTabCommand = new RelayCommand(() => SelectedTab = MainTab.Health);
        SelectAlertsTabCommand = new RelayCommand(() => SelectedTab = MainTab.Alerts);
        SelectGroupsTabCommand = new RelayCommand(() => SelectedTab = MainTab.Groups);
        SelectLocationsTabCommand = new RelayCommand(() => SelectedTab = MainTab.Locations);
        SelectRulesTabCommand = new RelayCommand(() => SelectedTab = MainTab.Rules);
        SelectTemplatesTabCommand = new RelayCommand(() => SelectedTab = MainTab.Templates);
        SelectMapsTabCommand = new RelayCommand(SelectDefaultMap);
        SelectNetworkMapTabCommand = new RelayCommand(() => SelectedTab = MainTab.MapsNetwork);
        SelectGeoMapTabCommand = new RelayCommand(() => SelectedTab = MainTab.MapsGeographical);
        SelectCustomMapsTabCommand = new RelayCommand(() => SelectedTab = MainTab.MapsCustom);
        SelectLogsTabCommand = new RelayCommand(() => SelectedTab = MainTab.LogsGraylog);
        RefreshCurrentTabCommand = new RelayCommand(RefreshCurrentTab);
        ClearCurrentTabFiltersCommand = new RelayCommand(ClearCurrentTabFilters);
        SettingsCommand = new RelayCommand(OpenSettings);
        ShowKeyboardShortcutsCommand = new RelayCommand(() => _windows.ShowKeyboardShortcuts());
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
        _rulesTab.ShowAlertsRequested += (_, rule) => ShowAlertsForRule(rule);

        // Ages are relative, so they have to be nudged even when nothing polls.
        _ageTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _ageTimer.Tick += (_, _) => RefreshAges();
        _ageTimer.Start();

        // Always ticking (not gated behind a connection), same as the tab
        // itself always being visible; before a connection exists this just
        // shows a static default until AlertMonitor.StartedAt is set.
        _refreshCountdown = new AutoRefreshTimer(() => OnPropertyChanged(nameof(NextRefreshText)));
        _refreshCountdown.Start();

        UpdateConnectionState();
    }

    public ObservableCollection<AlertItemViewModel> Alerts { get; }

    public ICollectionView AlertsView { get; }

    public RelayCommand RefreshCommand { get; }

    public AsyncRelayCommand AcknowledgeCommand { get; }

    public AsyncRelayCommand UnacknowledgeCommand { get; }

    /// <summary>Opens the alert's device in this app's own Device Details window (issue #158) - also what double-clicking a row does.</summary>
    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand OpenProcedureCommand { get; }

    /// <summary>Right-click "Filter by this rule" (issue #163) - the same Rule chip the Rules tab's alert badge sets.</summary>
    public RelayCommand FilterBySelectedRuleCommand { get; }

    /// <summary>Right-click "Filter by this device" (issue #163) - a Device chip alongside the Rule one.</summary>
    public RelayCommand FilterBySelectedDeviceCommand { get; }

    public RelayCommand ClearDeviceFilterCommand { get; }

    /// <summary>Opens the device-group filter dialog (issue #162).</summary>
    public RelayCommand ShowAlertFiltersCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>Copies the currently-filtered/visible alerts to the clipboard as CSV (issue #27).</summary>
    public RelayCommand CopyAlertsCsvCommand { get; }

    /// <summary>Saves the currently-filtered/visible alerts as a CSV file (issue #27).</summary>
    public RelayCommand ExportAlertsCsvCommand { get; }

    /// <summary>Re-fetches the faults for the selected alert.</summary>
    public RelayCommand ReloadDetailCommand { get; }

    public RelayCommand SelectDashboardTabCommand { get; }

    public RelayCommand SelectDevicesTabCommand { get; }

    public RelayCommand SelectHealthTabCommand { get; }

    public RelayCommand SelectAlertsTabCommand { get; }

    public RelayCommand SelectGroupsTabCommand { get; }

    public RelayCommand SelectLocationsTabCommand { get; }

    public RelayCommand SelectRulesTabCommand { get; }

    public RelayCommand SelectTemplatesTabCommand { get; }

    /// <summary>Clicking Maps itself - opens whichever map Settings names as the default.</summary>
    public RelayCommand SelectMapsTabCommand { get; }

    public RelayCommand SelectNetworkMapTabCommand { get; }

    public RelayCommand SelectGeoMapTabCommand { get; }

    public RelayCommand SelectCustomMapsTabCommand { get; }

    public RelayCommand SelectLogsTabCommand { get; }

    /// <summary>F5: refreshes whichever tab is currently showing.</summary>
    public RelayCommand RefreshCurrentTabCommand { get; }

    /// <summary>Ctrl+L: clears the filters on whichever tab is currently showing.</summary>
    public RelayCommand ClearCurrentTabFiltersCommand { get; }

    public RelayCommand SettingsCommand { get; }

    /// <summary>F1 or the header's keyboard button - lists every shortcut (#65).</summary>
    public RelayCommand ShowKeyboardShortcutsCommand { get; }

    public RelayCommand SignOutCommand { get; }

    public RelayCommand ExitCommand { get; }

    /// <summary>The device list, for the Devices tab's content to bind to.</summary>
    public DeviceListViewModel DeviceList => _deviceList;

    /// <summary>The sensor health list, for the Health tab's content to bind to.</summary>
    public HealthViewModel Health => _health;

    /// <summary>The dashboard widgets, for the Dashboard tab's content to bind to.</summary>
    public DashboardViewModel Dashboard => _dashboard;

    /// <summary>The device group list, for the Groups tab's content to bind to.</summary>
    public GroupsViewModel Groups => _groups;

    /// <summary>The location list, for the Locations tab's content to bind to.</summary>
    public LocationsViewModel Locations => _locations;

    /// <summary>The alert rule list, for the Rules tab's content to bind to.</summary>
    public RulesViewModel Rules => _rulesTab;

    /// <summary>The alert template list, for the Templates tab's content to bind to.</summary>
    public TemplatesViewModel Templates => _templates;

    /// <summary>The network map, for the Map tab's content to bind to.</summary>
    public NetworkMapViewModel NetworkMap => _networkMap;

    /// <summary>The geographical map, for Maps → Geographical to bind to.</summary>
    public GeoMapViewModel GeoMap => _geoMap;

    /// <summary>The custom maps, for Maps → Custom Maps to bind to.</summary>
    public CustomMapsViewModel CustomMaps => _customMaps;

    public LogsViewModel Logs => _logs;

    /// <summary>The Logs tab only shows while Graylog - its only source so far - is set up.</summary>
    public bool ShowLogsTab => _graylog.IsConfigured;

    /// <summary>The connected server's favicon, shown next to the tabs. Null until it loads, or if there isn't one.</summary>
    public BitmapImage? ServerLogo => _branding.Logo;

    /// <summary>
    /// What the shell header's logo slot actually shows: the server's own
    /// branding when <see cref="AppSettings.ShowServerLogo"/> is on and one
    /// has loaded, DashyNMS's own icon otherwise - see that setting's
    /// remarks for why someone would turn it off.
    /// </summary>
    public BitmapImage? HeaderLogo => _settings.Current.ShowServerLogo ? ServerLogo : AppIconLogo.Value;

    public bool HasHeaderLogo => HeaderLogo is not null;

    private void OnBrandingChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ServerLogo));
        OnPropertyChanged(nameof(HeaderLogo));
        OnPropertyChanged(nameof(HasHeaderLogo));
    }

    private void OnLogoSettingChanged(object? sender, AppSettings settings)
    {
        OnPropertyChanged(nameof(HeaderLogo));
        OnPropertyChanged(nameof(HasHeaderLogo));
    }

    public MainTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsDashboardTabSelected));
                OnPropertyChanged(nameof(IsDevicesTabSelected));
                OnPropertyChanged(nameof(IsDevicesFamilyTabSelected));
                OnPropertyChanged(nameof(IsHealthTabSelected));
                OnPropertyChanged(nameof(IsAlertsTabSelected));
                OnPropertyChanged(nameof(IsAlertsFamilyTabSelected));
                OnPropertyChanged(nameof(IsGroupsTabSelected));
                OnPropertyChanged(nameof(IsLocationsTabSelected));
                OnPropertyChanged(nameof(IsRulesTabSelected));
                OnPropertyChanged(nameof(IsTemplatesTabSelected));
                OnPropertyChanged(nameof(IsMapsFamilyTabSelected));
                OnPropertyChanged(nameof(IsNetworkMapTabSelected));
                OnPropertyChanged(nameof(IsGeoMapTabSelected));
                OnPropertyChanged(nameof(IsCustomMapsTabSelected));
                OnPropertyChanged(nameof(IsLogsFamilyTabSelected));
                OnPropertyChanged(nameof(IsGraylogLogsTabSelected));

                // Loaded once, lazily, the first time a tab is actually looked at.
                if (value == MainTab.Devices)
                {
                    _deviceList.OnShown();
                }
                else if (value == MainTab.Health)
                {
                    _health.OnShown();
                }
                else if (value == MainTab.Dashboard)
                {
                    _dashboard.OnShown();
                }
                else if (value == MainTab.Groups)
                {
                    _groups.OnShown();
                }
                else if (value == MainTab.Locations)
                {
                    _locations.OnShown();
                }
                else if (value == MainTab.Rules)
                {
                    _rulesTab.OnShown();
                }
                else if (value == MainTab.Templates)
                {
                    _templates.OnShown();
                }
                else if (value == MainTab.MapsNetwork)
                {
                    _networkMap.OnShown();
                }
                else if (value == MainTab.MapsGeographical)
                {
                    _geoMap.OnShown();
                }
                else if (value == MainTab.MapsCustom)
                {
                    _customMaps.OnShown();
                }

                // Unlike the others, Logs also needs telling when it's left -
                // its auto-update only runs while it's on screen.
                if (value == MainTab.LogsGraylog)
                {
                    _logs.OnShown();
                }
                else
                {
                    _logs.OnHidden();
                }
            }
        }
    }

    /// <summary>Graylog switched on or off in Settings: show or hide the Logs tab, leaving it first if it's the one showing.</summary>
    private void OnGraylogConfigurationChanged(object? sender, EventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (!_graylog.IsConfigured && SelectedTab == MainTab.LogsGraylog)
            {
                SelectedTab = MainTab.Dashboard;
            }

            OnPropertyChanged(nameof(ShowLogsTab));
        });
    }

    /// <summary>
    /// The main window was shown or hidden (to the tray, or minimised) -
    /// pauses or resumes anything that only runs while it's on screen (the
    /// Logs tab's auto-update).
    /// </summary>
    public void OnWindowVisibilityChanged(bool isVisible)
    {
        if (isVisible && SelectedTab == MainTab.LogsGraylog)
        {
            _logs.OnShown();
        }
        else
        {
            _logs.OnHidden();
        }
    }

    public bool IsDashboardTabSelected => SelectedTab == MainTab.Dashboard;

    public bool IsDevicesTabSelected => SelectedTab == MainTab.Devices;

    /// <summary>True for Devices itself or either of its hover-flyout sub-tabs (Groups, Locations) - keeps the Devices nav button highlighted while browsing either, since they are facets of device/inventory management rather than peers of it.</summary>
    public bool IsDevicesFamilyTabSelected => SelectedTab is MainTab.Devices or MainTab.Groups or MainTab.Locations;

    public bool IsHealthTabSelected => SelectedTab == MainTab.Health;

    public bool IsAlertsTabSelected => SelectedTab == MainTab.Alerts;

    /// <summary>True for Alerts itself or either of its hover-flyout sub-tabs (Rules, Templates) - same "family" pattern as <see cref="IsDevicesFamilyTabSelected"/>.</summary>
    public bool IsAlertsFamilyTabSelected => SelectedTab is MainTab.Alerts or MainTab.Rules or MainTab.Templates;

    public bool IsGroupsTabSelected => SelectedTab == MainTab.Groups;

    public bool IsLocationsTabSelected => SelectedTab == MainTab.Locations;

    public bool IsRulesTabSelected => SelectedTab == MainTab.Rules;

    public bool IsTemplatesTabSelected => SelectedTab == MainTab.Templates;

    /// <summary>True for any of the three maps - keeps the Maps nav button highlighted, same "family" pattern as Devices and Alerts.</summary>
    public bool IsMapsFamilyTabSelected => SelectedTab is MainTab.MapsNetwork or MainTab.MapsGeographical or MainTab.MapsCustom;

    public bool IsNetworkMapTabSelected => SelectedTab == MainTab.MapsNetwork;

    public bool IsGeoMapTabSelected => SelectedTab == MainTab.MapsGeographical;

    public bool IsCustomMapsTabSelected => SelectedTab == MainTab.MapsCustom;

    /// <summary>True for any Logs view (only Graylog so far) - keeps the Logs nav button highlighted, same "family" pattern as Maps.</summary>
    public bool IsLogsFamilyTabSelected => SelectedTab is MainTab.LogsGraylog;

    public bool IsGraylogLogsTabSelected => SelectedTab == MainTab.LogsGraylog;

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

    /// <summary>
    /// When set, only alerts raised by this rule show - the Rules tab's
    /// "alerts raised" badge sets it. Transient: not persisted with the other
    /// filters, and cleared by Clear like the rest.
    /// </summary>
    public AlertRule? FilterRule
    {
        get => _filterRule;
        set
        {
            if (SetProperty(ref _filterRule, value))
            {
                OnPropertyChanged(nameof(HasRuleFilter));
                OnPropertyChanged(nameof(FilterRuleName));
                OnFilterChanged();
            }
        }
    }

    public bool HasRuleFilter => _filterRule is not null;

    /// <summary>True when anything narrows the alert list - drives the toolbar's clear (✕) button, shown only when there's something to clear.</summary>
    public bool HasActiveFilters =>
        !ShowCritical || !ShowWarning || !ShowAcknowledged || HasRuleFilter || HasDeviceFilter
        || GroupFilter.HasActiveFilter || !string.IsNullOrWhiteSpace(SearchText);

    public string FilterRuleName => _filterRule?.Name ?? string.Empty;

    public RelayCommand ClearRuleFilterCommand { get; }

    /// <summary>
    /// When set, only alerts on this device show - the right-click "Filter by
    /// this device" action and Device Details' "Show alerts" both set it.
    /// A chip matching the Rule one, keyed by device id rather than text in
    /// the search box, so it can't also match other devices with similar
    /// names. Transient like <see cref="FilterRule"/>: not persisted.
    /// </summary>
    public bool HasDeviceFilter => _filterDeviceId is not null;

    public string FilterDeviceName => _filterDeviceName;

    /// <summary>
    /// Device group facet (issue #162) - same component as the Devices tab's
    /// Group filter, counting alerts rather than devices, fed by the shared
    /// <see cref="IDeviceGroupMembershipService"/>.
    /// </summary>
    public FilterFacet GroupFilter { get; }

    /// <summary>Drives the dot on the toolbar's filter button - the dialog's checkboxes aren't visible until it's opened.</summary>
    public bool IsGroupFilterActive => GroupFilter.HasActiveFilter;

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

    /// <summary>A short "45s" / "2:05" countdown to the next automatic refresh, aligned with every other tab's.</summary>
    public string NextRefreshText => PollAlignment.FormatRemaining(_monitor.SecondsUntilNextPoll());

    // ----------------------------------------------------------------- counts

    public int CriticalCount => Alerts.Count(a => a.Severity == AlertSeverity.Critical && a.State == AlertState.Active);

    public int WarningCount => Alerts.Count(a => a.Severity == AlertSeverity.Warning && a.State == AlertState.Active);

    public int AcknowledgedCount => Alerts.Count(a => a.State == AlertState.Acknowledged);

    public int TotalCount => Alerts.Count;

    public int VisibleCount => AlertsView.Cast<object>().Count();

    /// <summary>Drives the loading/empty/no-matches split on the Alerts grid (issue #16).</summary>
    public ListLoadState LoadState { get; } = new();

    // --------------------------------------------------------------- lifetime

    /// <summary>Called by the app once the session is established.</summary>
    public void OnConnected()
    {
        UpdateConnectionState();
        StatusMessage = "Connected. Waiting for the first poll...";
        RequestRefresh();

        // The Alerts tab is always live (not just when shown), so its group
        // filter needs membership from the start too.
        _groupMembership.EnsureStarted();

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
        else if (SelectedTab == MainTab.Dashboard)
        {
            _dashboard.OnShown();
        }
        else if (SelectedTab == MainTab.Groups)
        {
            _groups.OnShown();
        }
        else if (SelectedTab == MainTab.Locations)
        {
            _locations.OnShown();
        }
        else if (SelectedTab == MainTab.Rules)
        {
            _rulesTab.OnShown();
        }
        else if (SelectedTab == MainTab.Templates)
        {
            _templates.OnShown();
        }
        else if (SelectedTab == MainTab.MapsNetwork)
        {
            _networkMap.OnShown();
        }
        else if (SelectedTab == MainTab.MapsGeographical)
        {
            _geoMap.OnShown();
        }
        else if (SelectedTab == MainTab.MapsCustom)
        {
            _customMaps.OnShown();
        }
        else if (SelectedTab == MainTab.LogsGraylog)
        {
            _logs.OnShown();
        }
    }

    private static MainTab MapStartupTab(StartupTab tab) => tab switch
    {
        StartupTab.Devices => MainTab.Devices,
        StartupTab.Health => MainTab.Health,
        StartupTab.Alerts => MainTab.Alerts,
        StartupTab.Groups => MainTab.Groups,
        StartupTab.Locations => MainTab.Locations,
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
    /// Switches to the Alerts tab showing every alert the given rule has
    /// raised - all severities, active and acknowledged - regardless of the
    /// filters that were set. Used by the Rules tab's alert badges.
    /// </summary>
    public void ShowAlertsForRule(AlertRule rule)
    {
        SelectedTab = MainTab.Alerts;
        _suppressFilterPersistence = true;

        ShowCritical = true;
        ShowWarning = true;
        ShowUnknownSeverity = true;
        ShowAcknowledged = true;
        SearchText = string.Empty;
        SetDeviceFilter(null, null);
        GroupFilter.SetAllChecked(true, notify: false);
        FilterRule = rule;

        _suppressFilterPersistence = false;
        OnFilterChanged();
    }

    /// <summary>
    /// Clears every other filter and shows only this device's alerts via the
    /// Device chip, so every active or acknowledged alert against it is
    /// visible. Used by Device Details' and the Devices tab's "Show alerts".
    /// </summary>
    public void ShowAlertsForDevice(int deviceId, string deviceName)
    {
        SelectedTab = MainTab.Alerts;
        _suppressFilterPersistence = true;

        ShowCritical = true;
        ShowWarning = true;
        ShowUnknownSeverity = true;
        ShowAcknowledged = true;
        SearchText = string.Empty;
        FilterRule = null;
        GroupFilter.SetAllChecked(true, notify: false);
        SetDeviceFilter(deviceId, deviceName);

        _suppressFilterPersistence = false;
        OnFilterChanged();
    }

    private void SetDeviceFilter(int? deviceId, string? deviceName)
    {
        if (_filterDeviceId == deviceId)
        {
            return;
        }

        _filterDeviceId = deviceId;
        _filterDeviceName = deviceId is null ? string.Empty : deviceName ?? $"Device #{deviceId}";
        OnPropertyChanged(nameof(HasDeviceFilter));
        OnPropertyChanged(nameof(FilterDeviceName));
        OnFilterChanged();
    }

    /// <summary>
    /// Narrows to every alert raised by the same rule as the selected one.
    /// Builds the chip's rule from the alert row itself (id and name) rather
    /// than a rule-cache lookup - the chip only ever needs those two, and
    /// this way it works instantly and even if the rules list hasn't loaded.
    /// </summary>
    private void FilterBySelectedRule()
    {
        if (SelectedAlert is { } alert)
        {
            FilterRule = new AlertRule { Id = alert.RuleId, Name = alert.RuleName };
        }
    }

    private void FilterBySelectedDevice()
    {
        if (SelectedAlert is { } alert)
        {
            SetDeviceFilter(alert.DeviceId, alert.DeviceName);
        }
    }

    private void ShowAlertFilters()
    {
        _windows.ShowAlertFiltersDialog();

        // Cleared after the (modal) dialog closes, not while it's open, so
        // its search box doesn't visibly empty itself.
        GroupFilter.ClearSearch();
    }

    /// <summary>
    /// Rebuilds the group facet's counts - one per alert that can appear in
    /// this list at all (recovered alerts never do), not per device - from
    /// the current alerts against whatever membership is currently known.
    /// Called after every poll and whenever membership itself changes.
    /// </summary>
    private void RebuildGroupFilter()
    {
        var counts = new Dictionary<string, int>();

        foreach (var alert in Alerts)
        {
            if (alert.State == AlertState.Recovered)
            {
                continue;
            }

            foreach (var group in GroupsOrNone(alert.DeviceId))
            {
                counts[group] = counts.TryGetValue(group, out var existing) ? existing + 1 : 1;
            }
        }

        GroupFilter.Apply(counts.Select(kv => (kv.Key, GroupDisplayText(kv.Key), kv.Value)));
    }

    /// <summary>The single key used for a device in no group at all, so "not in a group" is a filterable option of its own - same as the Devices tab.</summary>
    private static readonly IReadOnlyList<string> NoGroupKey = new[] { string.Empty };

    private IReadOnlyList<string> GroupsOrNone(int deviceId) =>
        _groupMembership.GroupsFor(deviceId) is { Count: > 0 } names ? names : NoGroupKey;

    private static string GroupDisplayText(string group) =>
        string.IsNullOrWhiteSpace(group) ? "Not in a group" : group;

    private void OnGroupMembershipChanged(object? sender, EventArgs e)
    {
        RebuildGroupFilter();

        // A background refresh, not a filter change the user made - re-apply
        // without re-saving the persisted filter settings (which would also
        // raise settings-changed app-wide every few minutes for nothing).
        _suppressFilterPersistence = true;
        OnFilterChanged();
        _suppressFilterPersistence = false;
    }

    /// <summary>Apply() can flip HasActiveFilter without a checkbox toggle (e.g. the last unchecked group disappearing), so relay it directly.</summary>
    private void OnGroupFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterFacet.HasActiveFilter))
        {
            OnPropertyChanged(nameof(IsGroupFilterActive));
            OnPropertyChanged(nameof(HasActiveFilters));
        }
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
            _selfActions.Record(item.Id, AlertChangeKind.Acknowledged);
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

            LoadState.CompleteLoad(TotalCount, VisibleCount);
            return;
        }

        ErrorMessage = null;
        _lastUpdated = result.CompletedAt;

        ApplyAlerts(result.Alerts);

        StatusMessage = TotalCount == 0
            ? "No alerts."
            : $"{CriticalCount} critical, {WarningCount} warning, {AcknowledgedCount} acknowledged.";

        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(NextRefreshText));
        LoadState.CompleteLoad(TotalCount, VisibleCount);
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
        RebuildGroupFilter();

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
            selfActionKind: AlertChangeKind.Acknowledged,
            onAllSucceeded: () => AcknowledgeNote = string.Empty);

    private Task UnacknowledgeSelectedAsync()
        => RunBulkAsync(
            "Could not unacknowledge",
            item => _client.Alerts.UnmuteAsync(item.Id, "Unacknowledged from DashyNMS"),
            successCountVerb: "Returned to active",
            selfActionKind: AlertChangeKind.Unacknowledged);

    /// <summary>
    /// Applies <paramref name="action"/> to every selected alert concurrently.
    /// One alert failing does not stop the others; failures are reported
    /// together once everything has finished. Every alert that succeeds is
    /// recorded against <paramref name="selfActionKind"/> so the poll that
    /// picks up the change does not also raise a toast about it.
    /// </summary>
    private async Task RunBulkAsync(
        string failureTitle,
        Func<AlertItemViewModel, Task> action,
        string successCountVerb,
        AlertChangeKind selfActionKind,
        Action? onAllSucceeded = null)
    {
        if (!_session.IsConnected || SelectedAlerts.Count == 0)
        {
            return;
        }

        // Snapshot: RequestRefresh() at the end can reshuffle the live
        // collection, and the grid selection itself may change underneath us.
        var items = SelectedAlerts.ToList();

        if (items.Count > BulkConfirmThreshold && !_settings.Current.SuppressBulkAlertActionConfirmation)
        {
            var (title, message) = BulkConfirmText(selfActionKind, items.Count);
            var (confirmed, dontAskAgain) = _windows.ConfirmWithOptOut(title, message);

            if (dontAskAgain)
            {
                _settings.Current.SuppressBulkAlertActionConfirmation = true;
                _settings.Save();
            }

            if (!confirmed)
            {
                return;
            }
        }

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
                _selfActions.Record(item.Id, selfActionKind);
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

    /// <summary>Confirmation dialog text for a bulk action above <see cref="BulkConfirmThreshold"/>, phrased per action rather than sharing one generic verb.</summary>
    private static (string Title, string Message) BulkConfirmText(AlertChangeKind kind, int count) => kind switch
    {
        AlertChangeKind.Acknowledged => ("Acknowledge alerts", $"Acknowledge all {count} selected alerts?"),
        AlertChangeKind.Unacknowledged => ("Return alerts to active", $"Return all {count} selected alerts to active?"),
        _ => ("Confirm", $"Apply this to all {count} selected alerts?"),
    };

    private void OpenSelectedDevice()
    {
        if (SelectedAlert is { } alert)
        {
            _windows.ShowDeviceDetail(alert.DeviceId);
        }
    }

    /// <summary>
    /// Opens the rule's procedure/runbook URL (issue #141). The URL is
    /// whatever someone typed into the rule on the server, so it's treated as
    /// untrusted: only http/https ever reaches the shell - a file:, UNC or
    /// other-scheme "procedure" is ignored rather than launched.
    /// </summary>
    private void OpenProcedure(object? parameter)
    {
        var alert = parameter as AlertItemViewModel ?? SelectedAlert;

        if (alert?.ProcedureUrl is { } raw
            && Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            _windows.OpenUrl(url);
        }
        else if (alert?.HasProcedure == true)
        {
            _windows.ShowInformation(
                "Can't open procedure",
                $"This rule's procedure isn't a web link, so it won't be opened:\n\n{alert.ProcedureUrl}");
        }
    }

    /// <summary>
    /// Clicking Maps itself opens the Default map from Settings - Network,
    /// Geographical, or a specific custom map ("custom:{id}"). Anything
    /// unrecognised, including a custom map that's since been deleted,
    /// falls back to Network.
    /// </summary>
    private void SelectDefaultMap()
    {
        var setting = _settings.Current.DefaultMap;

        if (setting.StartsWith(AppSettings.DefaultMapCustomPrefix, StringComparison.Ordinal)
            && _customMaps.Maps.Any(m => m.Id == setting[AppSettings.DefaultMapCustomPrefix.Length..]))
        {
            SelectedTab = MainTab.MapsCustom;
            _customMaps.OpenMap(setting[AppSettings.DefaultMapCustomPrefix.Length..]);
            return;
        }

        SelectedTab = setting switch
        {
            AppSettings.DefaultMapGeographical => MainTab.MapsGeographical,
            _ => MainTab.MapsNetwork,
        };
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
                if (_dashboard.RefreshCommand.CanExecute(null))
                {
                    _dashboard.RefreshCommand.Execute(null);
                }

                break;

            case MainTab.Groups:
                if (_groups.RefreshCommand.CanExecute(null))
                {
                    _groups.RefreshCommand.Execute(null);
                }

                break;

            case MainTab.Locations:
                if (_locations.RefreshCommand.CanExecute(null))
                {
                    _locations.RefreshCommand.Execute(null);
                }

                break;

            case MainTab.Rules:
                if (_rulesTab.RefreshCommand.CanExecute(null))
                {
                    _rulesTab.RefreshCommand.Execute(null);
                }

                break;

            case MainTab.Templates:
                if (_templates.RefreshCommand.CanExecute(null))
                {
                    _templates.RefreshCommand.Execute(null);
                }

                break;

            case MainTab.MapsNetwork:
                if (_networkMap.RefreshCommand.CanExecute(null))
                {
                    _networkMap.RefreshCommand.Execute(null);
                }

                break;

            case MainTab.MapsGeographical:
                if (_geoMap.RefreshCommand.CanExecute(null))
                {
                    _geoMap.RefreshCommand.Execute(null);
                }

                break;

            case MainTab.MapsCustom:
                _ = _customMaps.RefreshAsync();
                break;

            case MainTab.LogsGraylog:
                if (_logs.RefreshCommand.CanExecute(null))
                {
                    _logs.RefreshCommand.Execute(null);
                }

                break;

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

            case MainTab.Groups:
                _groups.ClearFiltersCommand.Execute(null);
                break;

            case MainTab.Locations:
                _locations.ClearFiltersCommand.Execute(null);
                break;

            case MainTab.Rules:
                _rulesTab.ClearFiltersCommand.Execute(null);
                break;

            case MainTab.Templates:
                _templates.ClearFiltersCommand.Execute(null);
                break;

            case MainTab.MapsNetwork:
                _networkMap.ClearFiltersCommand.Execute(null);
                break;

            case MainTab.MapsGeographical:
                _geoMap.ClearFiltersCommand.Execute(null);
                break;

            case MainTab.LogsGraylog:
                _logs.ClearFiltersCommand.Execute(null);
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
        FilterRule = null;
        SetDeviceFilter(null, null);
        GroupFilter.SetAllChecked(true, notify: false);

        _suppressFilterPersistence = false;
        OnFilterChanged();
    }

    private static readonly string[] AlertCsvHeaders = { "Severity", "Device", "Alert", "State", "Age", "Note" };

    private string BuildAlertsCsv() => CsvWriter.ToCsv(
        AlertCsvHeaders,
        AlertsView.Cast<AlertItemViewModel>().Select(a => (IReadOnlyList<string>)new[]
        {
            a.SeverityText, a.DeviceName, a.RuleName, a.StateText, a.AgeText, a.Note ?? string.Empty,
        }));

    private void CopyAlertsCsv()
    {
        try
        {
            Clipboard.SetText(BuildAlertsCsv());
        }
        catch (ExternalException)
        {
            // Another process briefly holds the clipboard - not worth surfacing as an error.
        }
    }

    private void ExportAlertsCsv()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"alerts-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv",
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, BuildAlertsCsv());
        }
    }

    /// <summary>
    /// Shift-click on a severity badge: show only that severity, hiding the
    /// other. Leaves <see cref="ShowUnknownSeverity"/> alone - it has no
    /// badge of its own and stays permanently on.
    /// </summary>
    public void IsolateSeverity(AlertSeverity severity)
    {
        _showCritical = severity == AlertSeverity.Critical;
        _showWarning = severity == AlertSeverity.Warning;

        OnPropertyChanged(nameof(ShowCritical));
        OnPropertyChanged(nameof(ShowWarning));
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
        _groupMembership.Clear();

        Alerts.Clear();
        _index.Clear();
        SelectedAlert = null;
        SetDeviceFilter(null, null);
        RaiseCountsChanged();
        RebuildGroupFilter();

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

        if (_filterRule is not null && alert.RuleId != _filterRule.Id)
        {
            return false;
        }

        if (_filterDeviceId is { } deviceId && alert.DeviceId != deviceId)
        {
            return false;
        }

        if (!GroupFilter.AllowsAny(GroupsOrNone(alert.DeviceId)))
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
        OnPropertyChanged(nameof(HasActiveFilters));
        LoadState.UpdateVisibleCount(VisibleCount);

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
        OpenDeviceCommand.RaiseCanExecuteChanged();
        OpenProcedureCommand.RaiseCanExecuteChanged();
        FilterBySelectedRuleCommand.RaiseCanExecuteChanged();
        FilterBySelectedDeviceCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _ageTimer.Stop();
        _refreshCountdown.Dispose();
        _monitor.Polled -= OnPolled;
        _monitor.PollStarted -= OnPollStarted;
        _session.StateChanged -= OnSessionStateChanged;
        _graylog.ConfigurationChanged -= OnGraylogConfigurationChanged;
        _branding.Changed -= OnBrandingChanged;
        _settings.Changed -= OnLogoSettingChanged;
        _groupMembership.Changed -= OnGroupMembershipChanged;
        GroupFilter.PropertyChanged -= OnGroupFilterPropertyChanged;

        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;
    }
}

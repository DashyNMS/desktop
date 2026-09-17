using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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

/// <summary>Which section of the device window is showing.</summary>
public enum DeviceDetailSection
{
    Overview,
    Sensors,
    Ports,
    Resources,
    Vlans,
    Fdb,
    Arp,

    /// <summary>Both this device's currently active alerts and its historical alert log - see <see cref="Views.DeviceView"/>.</summary>
    Alerts,
    EventLog,

    /// <summary>Editable fields plus device-management actions (Rediscover now, Delete eventually) - a home for "change this device" rather than "view its data".</summary>
    Edit,
}

/// <summary>
/// View model behind a single device's detail window (see <see cref="Views.DeviceView"/>),
/// opened from the Devices tab instead of jumping straight to the LibreNMS
/// website. Device and sensor data both come from the already-running shared
/// <see cref="DeviceMonitor"/>/<see cref="SensorMonitor"/> - this window never
/// starts its own poll of either - and are simply filtered down to this one
/// device id. Alert history (<c>/api/v0/logs/alertlog</c>) is genuinely
/// per-device already, so it is fetched once when the window opens.
/// </summary>
public sealed class DeviceDetailViewModel : ObservableObject, IDisposable
{
    private readonly int _deviceId;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly SensorMonitor _sensorMonitor;
    private readonly AlertMonitor _alertMonitor;
    private readonly ILibreNmsClient _client;
    private readonly IAlertRuleCache _ruleFields;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILogger<DeviceDetailViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, SensorItemViewModel> _sensorIndex = new();
    private readonly HashSet<int> _loadedEventLogIds = new();

    /// <summary>
    /// Cancelled (and disposed) in <see cref="Dispose"/> - closing this
    /// device's window stops every in-flight section load (Ports/VLANs/FDB/
    /// ARP/Resources/Availability/Device groups/Event log/Alert history/
    /// Poller groups/Known OS list) instead of letting them run to
    /// completion and update a view model nothing is bound to anymore.
    /// </summary>
    private readonly CancellationTokenSource _loadCts = new();

    /// <summary>
    /// Port id -&gt; display name, populated whenever <see cref="Ports"/>
    /// loads. The FDB/ARP tabs read this directly (not a copy) at display
    /// time to show a friendly port name instead of a bare id - if either
    /// loads before Ports does (not rare in practice: Ports also fetches
    /// neighbours and IP addresses, so it is often the slower of the two),
    /// the affected rows show "Port {id}" until Ports finishes, at which
    /// point LoadPortsAsync calls RefreshPortName on each already-created
    /// row to correct it.
    /// </summary>
    private readonly Dictionary<int, string> _portNamesByPortId = new();

    /// <summary>
    /// VLAN internal id -&gt; the VLAN itself, populated whenever
    /// <see cref="LoadVlansAsync"/> loads. FdbItemViewModel reads this
    /// directly (not a copy) to resolve FdbEntry.VlanId - see its own
    /// remarks, and LoadVlansAsync's, for why this is a device-filtered
    /// slice of a fleet-wide fetch rather than a per-device one.
    /// </summary>
    private readonly Dictionary<int, Vlan> _vlansById = new();

    private const int EventLogPageSize = 50;
    private const int MaxOutagesShown = 10;
    private const int AvailabilityTimelineDays = 30;

    /// <summary>Always present regardless of what (if anything) the server returns - see AddDeviceViewModel's own copy of this same idea.</summary>
    private static readonly PollerGroup DefaultPollerGroup = new() { Id = 0, GroupName = "Default (poller 0)" };

    private int _eventLogLimit = EventLogPageSize;

    private double? _availability1Day;
    private double? _availability7Day;
    private double? _availability30Day;
    private double? _availability1Year;

    private Device? _device;
    private bool _isUnderMaintenance;
    private bool _isBusy;
    private bool _isRediscovering;
    private bool _isDeleting;
    private string? _errorMessage;
    private string _editHostname = string.Empty;
    private string _editLocation = string.Empty;
    private string _editDisplayName = string.Empty;
    private string _editType = string.Empty;
    private string _editPurpose = string.Empty;
    private bool _editOverrideSysLocation;
    private string _editContact = string.Empty;
    private bool _editOverrideSysContact;
    private string _editNotes = string.Empty;
    private bool _editDisabled;
    private bool _editIgnore;
    private bool _editIgnoreStatus;
    private PollerGroup _selectedPollerGroup = DefaultPollerGroup;
    private bool _hasLoadedPollerGroupsOnce;

    // SNMP editing is opt-in (EditChangeSnmp) and never pre-filled from the
    // current device - see EditChangeSnmp's own remarks for why.
    private bool _editChangeSnmp;
    private bool _editIsPingOnly;
    private bool _editIsSnmpV2c = true;
    private bool _editIsSnmpV1;
    private bool _editIsSnmpV3;
    private string _editCommunity = string.Empty;
    private string _editAuthLevel = "authPriv";
    private string _editAuthName = string.Empty;
    private string _editAuthPass = string.Empty;
    private string _editAuthAlgo = "SHA";
    private string _editCryptoPass = string.Empty;
    private string _editCryptoAlgo = "AES";
    private string _editSnmpOs = "ping";
    private string _editSysNameOverride = string.Empty;
    private string _editHardwareOverride = string.Empty;
    private string _editPort = string.Empty;
    private string _editTransport = string.Empty;

    private bool _isSavingEdit;
    private string? _editErrorMessage;
    private string? _editSuccessMessage;
    private DeviceDetailSection _selectedSection = DeviceDetailSection.Overview;
    private string _eventLogSearchText = string.Empty;
    private string _fdbSearchText = string.Empty;
    private string _arpSearchText = string.Empty;
    private string _portSearchText = string.Empty;
    private string _sensorSearchText = string.Empty;
    private string _vlanSearchText = string.Empty;

    // Each section here loads independently and asynchronously (see the
    // constructor's fire-and-forget Load*Async calls), so "Count == 0" alone
    // cannot tell a device that genuinely has none of something apart from
    // one whose fetch just has not landed yet. These flip once, the first
    // time that section's load actually completes (success or failure - a
    // failed fetch still means there is nothing confirmed to show), and back
    // every IsLoadingX/HasVisibleX property below.
    private bool _hasLoadedSensors;
    private bool _hasLoadedPorts;
    private bool _hasLoadedVlans;
    private bool _hasLoadedFdb;
    private bool _hasLoadedArp;
    private bool _hasLoadedResources;
    private bool _hasLoadedActiveAlerts;
    private bool _hasLoadedAlertHistory;
    private bool _hasLoadedEventLog;

    // Starts false, not true: until the first page has actually loaded and
    // said so, there is nothing confirmed to load more of. Defaulting this to
    // true let a "load more" fired before that first page finished (e.g. a
    // ScrollChanged on the still-empty grid) race the initial load and see
    // every entry as a duplicate, which read as "pagination is broken" and
    // latched this false for good before the user ever got to scroll for real.
    private bool _hasMoreEventLog;
    private bool _isLoadingMoreEventLog;

    public DeviceDetailViewModel(
        int deviceId,
        DeviceMonitor deviceMonitor,
        SensorMonitor sensorMonitor,
        AlertMonitor alertMonitor,
        IDeviceCache deviceCache,
        ILibreNmsClient client,
        IAlertRuleCache ruleFields,
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        ILogger<DeviceDetailViewModel> logger)
    {
        _deviceId = deviceId;
        _deviceMonitor = deviceMonitor;
        _sensorMonitor = sensorMonitor;
        _alertMonitor = alertMonitor;
        _client = client;
        _ruleFields = ruleFields;
        _session = session;
        _settings = settings;
        _windows = windows;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Sensors = new ObservableCollection<SensorItemViewModel>();
        SensorGroups = new ObservableCollection<SensorGroupViewModel>();
        AlertHistory = new ObservableCollection<AlertLogItemViewModel>();
        ActiveAlerts = new ObservableCollection<ActiveAlertItemViewModel>();
        Ports = new BatchObservableCollection<PortItemViewModel>();
        Processors = new ObservableCollection<ProcessorItemViewModel>();
        Mempools = new ObservableCollection<MempoolItemViewModel>();
        Storage = new ObservableCollection<StorageItemViewModel>();
        Outages = new ObservableCollection<OutageItemViewModel>();
        AvailabilityTimeline = new ObservableCollection<OutageDayViewModel>();
        DeviceGroups = new ObservableCollection<DeviceGroupItemViewModel>();
        VlanEntries = new BatchObservableCollection<VlanItemViewModel>();
        FdbEntries = new BatchObservableCollection<FdbItemViewModel>();
        ArpEntries = new BatchObservableCollection<ArpItemViewModel>();
        EventLog = new BatchObservableCollection<EventLogItemViewModel>();
        PollerGroups = new ObservableCollection<PollerGroup> { DefaultPollerGroup };

        PortsView = CollectionViewSource.GetDefaultView(Ports);
        PortsView.Filter = FilterPortEntry;

        VlansView = CollectionViewSource.GetDefaultView(VlanEntries);
        VlansView.Filter = FilterVlanEntry;

        FdbView = CollectionViewSource.GetDefaultView(FdbEntries);
        FdbView.Filter = FilterFdbEntry;

        ArpView = CollectionViewSource.GetDefaultView(ArpEntries);
        ArpView.Filter = FilterArpEntry;

        // Filter only - no grouping, so this does not run into the DataGrid
        // grouping/full-width fight the Sensors tab did.
        EventLogView = CollectionViewSource.GetDefaultView(EventLog);
        EventLogView.Filter = FilterEventLogEntry;

        ShowAlertsCommand = new RelayCommand(() => _windows.ShowAlertsForDevice(_device?.Hostname ?? Name));
        ShowDevicesForLocationCommand = new RelayCommand(ShowDevicesForLocation, () => HasLocation);

        OpenWebHttpCommand = new RelayCommand(() => OpenExternal("http"), () => CanOpenExternally);
        OpenWebHttpsCommand = new RelayCommand(() => OpenExternal("https"), () => CanOpenExternally);
        OpenTelnetCommand = new RelayCommand(() => OpenExternal("telnet"), () => CanOpenExternally);
        OpenSshCommand = new RelayCommand(() => OpenExternal("ssh"), () => CanOpenExternally);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _session.IsConnected && !IsBusy);
        RediscoverCommand = new AsyncRelayCommand(RediscoverAsync, () => _session.IsConnected && !IsRediscovering);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => _session.IsConnected && !IsDeleting);

        SelectOverviewCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Overview);
        SelectSensorsCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Sensors);
        SelectPortsCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Ports);
        SelectResourcesCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Resources);
        SelectVlansCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Vlans);
        SelectFdbCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Fdb);
        SelectArpCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Arp);
        SelectAlertsCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.Alerts);
        SelectEventLogCommand = new RelayCommand(() => SelectedSection = DeviceDetailSection.EventLog);
        SelectEditCommand = new RelayCommand(SelectEdit);

        SaveEditCommand = new AsyncRelayCommand(SaveEditAsync, () => !IsSavingEdit);

        LoadMoreEventLogCommand = new AsyncRelayCommand(LoadMoreEventLogAsync, () => HasMoreEventLog && !IsLoadingMoreEventLog);

        // Shows whatever is already cached instantly, rather than a blank
        // window until the next shared poll lands.
        _device = deviceCache.Get(deviceId);

        RecordRecentlyViewed();

        _deviceMonitor.Polled += OnDevicePolled;
        _sensorMonitor.Polled += OnSensorPolled;
        _alertMonitor.Polled += OnAlertsPolled;

        // All three monitors are almost certainly already running - the
        // Devices tab that opened this window depends on DeviceMonitor, and
        // AlertMonitor starts at sign-in - but Start() is idempotent, and a
        // Sensors widget being the only prior consumer of SensorMonitor
        // should not leave this window's sensor list empty.
        _deviceMonitor.Start();
        _sensorMonitor.Start();
        _alertMonitor.Start();
        _deviceMonitor.RequestRefresh();
        _sensorMonitor.RequestRefresh();
        _alertMonitor.RequestRefresh();

        _ = LoadAlertHistoryAsync();
        _ = LoadPortsAsync();
        _ = LoadResourcesAsync();
        _ = LoadAvailabilityAsync();
        _ = LoadDeviceGroupsAsync();
        _ = LoadVlansAsync();
        _ = LoadFdbAsync();
        _ = LoadArpAsync();
        _ = LoadEventLogAsync();
    }

    /// <summary>
    /// Bumps this device to the top of Settings > RecentlyViewedDevices,
    /// trimming to <see cref="AppSettings.RecentlyViewedDeviceCount"/>. Only
    /// runs when a new Device View is actually constructed - reactivating an
    /// already-open one (see WindowService.ShowDeviceDetail) does not bump
    /// it again, a minor gap not worth a new dependency to close.
    /// </summary>
    private void RecordRecentlyViewed()
    {
        var recent = _settings.Current.RecentlyViewedDevices;

        recent.RemoveAll(d => d.DeviceId == _deviceId);
        recent.Insert(0, new RecentlyViewedDevice
        {
            DeviceId = _deviceId,
            DisplayName = _device?.BestName,
            ViewedAt = DateTimeOffset.Now,
        });

        while (recent.Count > _settings.Current.RecentlyViewedDeviceCount)
        {
            recent.RemoveAt(recent.Count - 1);
        }

        _settings.Save();
    }

    /// <summary>
    /// Strips this device out of Settings > RecentlyViewedDevices and
    /// PinnedDevices after it has been deleted from LibreNMS - both are
    /// stored by device id, so a deleted device would otherwise keep
    /// appearing on the Devices tab and dashboard widgets that read them
    /// until the id happened to get reused for something else.
    /// </summary>
    private void ForgetPinnedAndRecentlyViewed()
    {
        var current = _settings.Current;
        var removedRecent = current.RecentlyViewedDevices.RemoveAll(d => d.DeviceId == _deviceId) > 0;
        var removedPinned = current.PinnedDevices.RemoveAll(p => p.DeviceId == _deviceId) > 0;

        if (removedRecent || removedPinned)
        {
            _settings.Save();
        }
    }

    public ObservableCollection<SensorItemViewModel> Sensors { get; }

    /// <summary>
    /// The same sensors, bucketed for display by
    /// <see cref="SensorItemViewModel.GroupKey"/> - e.g. every reading for
    /// one transceiver, or a PSU's voltage/current/power that all share a
    /// name - so related readings read as one thing instead of scattered
    /// rows. Built explicitly rather than via an ICollectionView's grouping:
    /// a grouped DataGrid does not lay its rows out at full width without a
    /// fight, and this list needs no sorting or selection to justify one.
    /// </summary>
    public ObservableCollection<SensorGroupViewModel> SensorGroups { get; }

    public ObservableCollection<AlertLogItemViewModel> AlertHistory { get; }

    public ObservableCollection<ActiveAlertItemViewModel> ActiveAlerts { get; }

    public BatchObservableCollection<PortItemViewModel> Ports { get; }

    /// <summary>Ports, filtered by <see cref="PortSearchText"/>. What the Ports tab actually binds to.</summary>
    public ICollectionView PortsView { get; }

    public BatchObservableCollection<VlanItemViewModel> VlanEntries { get; }

    /// <summary>The VLANs, filtered by <see cref="VlanSearchText"/>. What the VLANs tab actually binds to.</summary>
    public ICollectionView VlansView { get; }

    public ObservableCollection<ProcessorItemViewModel> Processors { get; }

    public ObservableCollection<MempoolItemViewModel> Mempools { get; }

    public ObservableCollection<StorageItemViewModel> Storage { get; }

    /// <summary>
    /// The device's recorded downtime incidents, newest first, capped to
    /// <see cref="MaxOutagesShown"/> - a long-lived device can accumulate a
    /// lot of these, and only the recent ones are actually useful at a glance.
    /// </summary>
    public ObservableCollection<OutageItemViewModel> Outages { get; }

    /// <summary>
    /// One entry per of the last <see cref="AvailabilityTimelineDays"/> days,
    /// oldest first - a compact status-page-style history bar, built from the
    /// same outage data as <see cref="Outages"/> rather than a second fetch.
    /// </summary>
    public ObservableCollection<OutageDayViewModel> AvailabilityTimeline { get; }

    /// <summary>Every LibreNMS device group this device belongs to - most devices are in none.</summary>
    public ObservableCollection<DeviceGroupItemViewModel> DeviceGroups { get; }

    /// <summary>Whether the Overview's Device Groups card has anything to show at all - it should not appear for a device in no groups.</summary>
    public bool HasDeviceGroups => DeviceGroups.Count > 0;

    public BatchObservableCollection<FdbItemViewModel> FdbEntries { get; }

    /// <summary>The FDB, filtered by <see cref="FdbSearchText"/>. What the FDB tab actually binds to.</summary>
    public ICollectionView FdbView { get; }

    public BatchObservableCollection<ArpItemViewModel> ArpEntries { get; }

    /// <summary>The ARP table, filtered by <see cref="ArpSearchText"/>. What the ARP tab actually binds to.</summary>
    public ICollectionView ArpView { get; }

    public BatchObservableCollection<EventLogItemViewModel> EventLog { get; }

    /// <summary>The event log, filtered by <see cref="EventLogSearchText"/>. What the Event log tab actually binds to.</summary>
    public ICollectionView EventLogView { get; }

    public RelayCommand ShowAlertsCommand { get; }

    /// <summary>Closes this window and shows the Devices tab isolated down to this device's own location - see <see cref="IWindowService.ShowDevicesFilteredByLocation"/>.</summary>
    public RelayCommand ShowDevicesForLocationCommand { get; }

    /// <summary>"Open in" header buttons - see <see cref="OpenExternal"/> for how the target address is picked.</summary>
    public RelayCommand OpenWebHttpCommand { get; }

    public RelayCommand OpenWebHttpsCommand { get; }

    public RelayCommand OpenTelnetCommand { get; }

    public RelayCommand OpenSshCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Queues an on-demand LibreNMS rediscovery of this device - see <see cref="RediscoverAsync"/>.</summary>
    public AsyncRelayCommand RediscoverCommand { get; }

    /// <summary>Permanently removes this device from LibreNMS, after confirmation - see <see cref="DeleteAsync"/>.</summary>
    public AsyncRelayCommand DeleteCommand { get; }

    public RelayCommand SelectOverviewCommand { get; }

    public RelayCommand SelectSensorsCommand { get; }

    public RelayCommand SelectPortsCommand { get; }

    public RelayCommand SelectResourcesCommand { get; }

    public RelayCommand SelectVlansCommand { get; }

    public RelayCommand SelectFdbCommand { get; }

    public RelayCommand SelectArpCommand { get; }

    public RelayCommand SelectAlertsCommand { get; }

    public RelayCommand SelectEventLogCommand { get; }

    /// <summary>Navigates to the Edit section and refreshes its draft fields from the current device - see <see cref="SelectEdit"/>.</summary>
    public RelayCommand SelectEditCommand { get; }

    public AsyncRelayCommand LoadMoreEventLogCommand { get; }

    /// <summary>Saves whichever Edit fields actually changed - see <see cref="SaveEditAsync"/>.</summary>
    public AsyncRelayCommand SaveEditCommand { get; }

    /// <summary>Free-text filter over a port's name, description and alias.</summary>
    public string PortSearchText
    {
        get => _portSearchText;
        set
        {
            if (SetProperty(ref _portSearchText, value))
            {
                PortsView.Refresh();
                OnPropertyChanged(nameof(HasVisiblePorts));
                OnPropertyChanged(nameof(ShowPortsNoMatchesMessage));
            }
        }
    }

    /// <summary>Free-text filter over a VLAN's number and name.</summary>
    public string VlanSearchText
    {
        get => _vlanSearchText;
        set
        {
            if (SetProperty(ref _vlanSearchText, value))
            {
                VlansView.Refresh();
                OnPropertyChanged(nameof(HasVisibleVlans));
                OnPropertyChanged(nameof(ShowVlansNoMatchesMessage));
            }
        }
    }

    /// <summary>Free-text filter over a sensor's device name, description and id - narrows <see cref="SensorGroups"/> itself, since the tab groups rather than lists sensors flatly.</summary>
    public string SensorSearchText
    {
        get => _sensorSearchText;
        set
        {
            if (SetProperty(ref _sensorSearchText, value))
            {
                RebuildSensorGroups(force: true);
            }
        }
    }

    /// <summary>Free-text filter over the FDB's MAC address, port and VLAN.</summary>
    public string FdbSearchText
    {
        get => _fdbSearchText;
        set
        {
            if (SetProperty(ref _fdbSearchText, value))
            {
                FdbView.Refresh();
                OnPropertyChanged(nameof(HasVisibleFdbEntries));
                OnPropertyChanged(nameof(ShowFdbNoMatchesMessage));
            }
        }
    }

    /// <summary>Free-text filter over the ARP table's IP, MAC address and port.</summary>
    public string ArpSearchText
    {
        get => _arpSearchText;
        set
        {
            if (SetProperty(ref _arpSearchText, value))
            {
                ArpView.Refresh();
                OnPropertyChanged(nameof(HasVisibleArpEntries));
                OnPropertyChanged(nameof(ShowArpNoMatchesMessage));
            }
        }
    }

    /// <summary>Free-text filter over the event log's message, type and username - applied client-side over whatever pages have been loaded so far.</summary>
    public string EventLogSearchText
    {
        get => _eventLogSearchText;
        set
        {
            if (SetProperty(ref _eventLogSearchText, value))
            {
                EventLogView.Refresh();
                OnPropertyChanged(nameof(HasVisibleEventLog));
                OnPropertyChanged(nameof(ShowEventLogNoMatchesMessage));
            }
        }
    }

    /// <summary>
    /// True while the last full page fetched actually contained new entries -
    /// once a page comes back empty, or entirely duplicates what is already
    /// loaded (a sign paging is not advancing - see <see cref="ILogsApi.ListEventLogAsync"/>),
    /// this goes false and "load more" stops firing.
    /// </summary>
    public bool HasMoreEventLog
    {
        get => _hasMoreEventLog;
        private set
        {
            if (SetProperty(ref _hasMoreEventLog, value))
            {
                LoadMoreEventLogCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoadingMoreEventLog
    {
        get => _isLoadingMoreEventLog;
        private set
        {
            if (SetProperty(ref _isLoadingMoreEventLog, value))
            {
                LoadMoreEventLogCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DeviceDetailSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(IsOverviewSelected));
                OnPropertyChanged(nameof(IsSensorsSelected));
                OnPropertyChanged(nameof(IsPortsSelected));
                OnPropertyChanged(nameof(IsResourcesSelected));
                OnPropertyChanged(nameof(IsVlansSelected));
                OnPropertyChanged(nameof(IsFdbSelected));
                OnPropertyChanged(nameof(IsArpSelected));
                OnPropertyChanged(nameof(IsAlertsSelected));
                OnPropertyChanged(nameof(IsEventLogSelected));
                OnPropertyChanged(nameof(IsEditSelected));
            }
        }
    }

    public bool IsOverviewSelected => SelectedSection == DeviceDetailSection.Overview;

    public bool IsSensorsSelected => SelectedSection == DeviceDetailSection.Sensors;

    public bool IsPortsSelected => SelectedSection == DeviceDetailSection.Ports;

    public bool IsResourcesSelected => SelectedSection == DeviceDetailSection.Resources;

    public bool IsVlansSelected => SelectedSection == DeviceDetailSection.Vlans;

    public bool IsFdbSelected => SelectedSection == DeviceDetailSection.Fdb;

    public bool IsArpSelected => SelectedSection == DeviceDetailSection.Arp;

    public bool IsAlertsSelected => SelectedSection == DeviceDetailSection.Alerts;

    public bool IsEventLogSelected => SelectedSection == DeviceDetailSection.EventLog;

    public bool IsEditSelected => SelectedSection == DeviceDetailSection.Edit;

    // -------------------------------------------------------------------- edit

    /// <summary>
    /// A draft copy of the fields LibreNMS lets you change via update_device_field
    /// (plus a rename, a distinct operation - see <see cref="SaveEditAsync"/>),
    /// separate from the live <see cref="Location"/>/etc. so switching to the
    /// Edit section always starts from the current values (see
    /// <see cref="SelectEdit"/>) without the raw device text properties
    /// elsewhere in this view model needing to become editable themselves.
    /// </summary>
    public string EditHostname
    {
        get => _editHostname;
        set => SetProperty(ref _editHostname, value);
    }

    /// <summary>
    /// Changing this away from the device's current Location automatically
    /// ticks <see cref="EditOverrideSysLocation"/> - editing the field is a
    /// clear signal you want it to actually take effect, and forgetting to
    /// also tick the override otherwise means the typed value is silently
    /// ignored in favour of the device's own reported sysLocation. Never
    /// auto-unticks: once on, staying on is the safer default.
    /// </summary>
    public string EditLocation
    {
        get => _editLocation;
        set
        {
            if (SetProperty(ref _editLocation, value) && value.Trim() != (_device?.Location ?? string.Empty))
            {
                EditOverrideSysLocation = true;
            }
        }
    }

    /// <summary>Overrides the display name shown throughout the app - see <see cref="DeviceNameStyle"/>, which already prefers this over sysName/hostname once set.</summary>
    public string EditDisplayName
    {
        get => _editDisplayName;
        set => SetProperty(ref _editDisplayName, value);
    }

    public string EditType
    {
        get => _editType;
        set => SetProperty(ref _editType, value);
    }

    public string EditPurpose
    {
        get => _editPurpose;
        set => SetProperty(ref _editPurpose, value);
    }

    /// <summary>Forces <see cref="EditLocation"/> to win over the device's own reported sysLocation.</summary>
    public bool EditOverrideSysLocation
    {
        get => _editOverrideSysLocation;
        set => SetProperty(ref _editOverrideSysLocation, value);
    }

    /// <summary>Same auto-tick behaviour as <see cref="EditLocation"/>/<see cref="EditOverrideSysLocation"/>, for the sysContact equivalent.</summary>
    public string EditContact
    {
        get => _editContact;
        set
        {
            if (SetProperty(ref _editContact, value) && value.Trim() != (_device?.Contact ?? string.Empty))
            {
                EditOverrideSysContact = true;
            }
        }
    }

    /// <summary>Forces <see cref="EditContact"/> to win over the device's own reported sysContact, mirroring <see cref="EditOverrideSysLocation"/>.</summary>
    public bool EditOverrideSysContact
    {
        get => _editOverrideSysContact;
        set => SetProperty(ref _editOverrideSysContact, value);
    }

    public string EditNotes
    {
        get => _editNotes;
        set => SetProperty(ref _editNotes, value);
    }

    /// <summary>Stops polling (and therefore alerting) for this device entirely.</summary>
    public bool EditDisabled
    {
        get => _editDisabled;
        set => SetProperty(ref _editDisabled, value);
    }

    /// <summary>Keeps polling, but suppresses alerts for this device.</summary>
    public bool EditIgnore
    {
        get => _editIgnore;
        set => SetProperty(ref _editIgnore, value);
    }

    /// <summary>Excludes this device from fleet-wide up/down availability figures without affecting polling or alerting.</summary>
    public bool EditIgnoreStatus
    {
        get => _editIgnoreStatus;
        set => SetProperty(ref _editIgnoreStatus, value);
    }

    /// <summary>
    /// Always has at least <see cref="DefaultPollerGroup"/>; whatever else
    /// LibreNMS reports gets appended the first time the Edit section is
    /// opened (see <see cref="SelectEdit"/>) - loaded lazily rather than for
    /// every Device Details window, since most opens never visit Edit.
    /// </summary>
    public ObservableCollection<PollerGroup> PollerGroups { get; }

    public PollerGroup SelectedPollerGroup
    {
        get => _selectedPollerGroup;
        set => SetProperty(ref _selectedPollerGroup, value);
    }

    /// <summary>Suggestions for <see cref="EditSnmpOs"/> - see AddDeviceViewModel.KnownOperatingSystems for the same idea/reasoning.</summary>
    public ObservableCollection<string> KnownOperatingSystems { get; } = new() { "ping" };

    // ---------------------------------------------------------- SNMP editing

    /// <summary>
    /// Gates the whole SNMP sub-form: unchecked (the default every time Edit
    /// opens), none of the SNMP fields below are sent on Save regardless of
    /// their values. LibreNMS's read API does not return stored SNMP
    /// credentials (nor should a UI echo secrets back), so this form can only
    /// ever set new values, never show current ones - without this gate,
    /// saving the Edit form for an unrelated reason (e.g. just Location)
    /// could silently blank out or reset a device's working SNMP config.
    /// </summary>
    public bool EditChangeSnmp
    {
        get => _editChangeSnmp;
        set
        {
            if (SetProperty(ref _editChangeSnmp, value))
            {
                OnPropertyChanged(nameof(ShowSnmpEditFields));
            }
        }
    }

    public bool ShowSnmpEditFields => EditChangeSnmp;

    public bool EditIsSnmpEnabled
    {
        get => !_editIsPingOnly;
        set { if (value) SetEditPingOnly(false); }
    }

    public bool EditIsPingOnly
    {
        get => _editIsPingOnly;
        set { if (value) SetEditPingOnly(true); }
    }

    public bool ShowEditSnmpFields => EditIsSnmpEnabled;

    public bool ShowEditPingOnlyFields => EditIsPingOnly;

    private void SetEditPingOnly(bool pingOnly)
    {
        if (_editIsPingOnly == pingOnly)
        {
            return;
        }

        _editIsPingOnly = pingOnly;
        OnPropertyChanged(nameof(EditIsSnmpEnabled));
        OnPropertyChanged(nameof(EditIsPingOnly));
        OnPropertyChanged(nameof(ShowEditSnmpFields));
        OnPropertyChanged(nameof(ShowEditPingOnlyFields));
    }

    public bool EditIsSnmpV2c
    {
        get => _editIsSnmpV2c;
        set { if (value) SetEditSnmpVersion(v2c: true); }
    }

    public bool EditIsSnmpV1
    {
        get => _editIsSnmpV1;
        set { if (value) SetEditSnmpVersion(v1: true); }
    }

    public bool EditIsSnmpV3
    {
        get => _editIsSnmpV3;
        set { if (value) SetEditSnmpVersion(v3: true); }
    }

    public bool ShowEditCommunity => EditIsSnmpV1 || EditIsSnmpV2c;

    public bool ShowEditV3Fields => EditIsSnmpV3;

    private void SetEditSnmpVersion(bool v2c = false, bool v1 = false, bool v3 = false)
    {
        _editIsSnmpV2c = v2c;
        _editIsSnmpV1 = v1;
        _editIsSnmpV3 = v3;

        OnPropertyChanged(nameof(EditIsSnmpV2c));
        OnPropertyChanged(nameof(EditIsSnmpV1));
        OnPropertyChanged(nameof(EditIsSnmpV3));
        OnPropertyChanged(nameof(ShowEditCommunity));
        OnPropertyChanged(nameof(ShowEditV3Fields));
    }

    public string EditCommunity
    {
        get => _editCommunity;
        set => SetProperty(ref _editCommunity, value);
    }

    public string EditAuthLevel
    {
        get => _editAuthLevel;
        set => SetProperty(ref _editAuthLevel, value);
    }

    public string EditAuthName
    {
        get => _editAuthName;
        set => SetProperty(ref _editAuthName, value);
    }

    public string EditAuthPass
    {
        get => _editAuthPass;
        set => SetProperty(ref _editAuthPass, value);
    }

    public string EditAuthAlgo
    {
        get => _editAuthAlgo;
        set => SetProperty(ref _editAuthAlgo, value);
    }

    public string EditCryptoPass
    {
        get => _editCryptoPass;
        set => SetProperty(ref _editCryptoPass, value);
    }

    public string EditCryptoAlgo
    {
        get => _editCryptoAlgo;
        set => SetProperty(ref _editCryptoAlgo, value);
    }

    /// <summary>Ping-only OS short name - see AddDeviceViewModel.Os for the same idea at add time.</summary>
    public string EditSnmpOs
    {
        get => _editSnmpOs;
        set => SetProperty(ref _editSnmpOs, value);
    }

    public string EditSysNameOverride
    {
        get => _editSysNameOverride;
        set => SetProperty(ref _editSysNameOverride, value);
    }

    public string EditHardwareOverride
    {
        get => _editHardwareOverride;
        set => SetProperty(ref _editHardwareOverride, value);
    }

    public string EditPort
    {
        get => _editPort;
        set => SetProperty(ref _editPort, value);
    }

    public string EditTransport
    {
        get => _editTransport;
        set => SetProperty(ref _editTransport, value);
    }

    public bool IsSavingEdit
    {
        get => _isSavingEdit;
        private set
        {
            if (SetProperty(ref _isSavingEdit, value))
            {
                SaveEditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? EditErrorMessage
    {
        get => _editErrorMessage;
        private set
        {
            if (SetProperty(ref _editErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasEditError));
            }
        }
    }

    public bool HasEditError => !string.IsNullOrEmpty(_editErrorMessage);

    public string? EditSuccessMessage
    {
        get => _editSuccessMessage;
        private set
        {
            if (SetProperty(ref _editSuccessMessage, value))
            {
                OnPropertyChanged(nameof(HasEditSuccess));
            }
        }
    }

    public bool HasEditSuccess => !string.IsNullOrEmpty(_editSuccessMessage);

    // ------------------------------------------------------------------ device

    public int DeviceId => _deviceId;

    public string Name => _device is null
        ? $"Device {_deviceId}"
        : _settings.Current.DeviceNameStyle.Resolve(_device, _device.Hostname);

    public string? AlternateName => _device is null
        ? null
        : _settings.Current.DeviceNameStyle.ResolveSecondary(_device, _device.Hostname, Name);

    public bool HasAlternateName => AlternateName is not null;

    /// <summary>The raw SNMP system description, e.g. "Onyx,SN2010M,SWv3.10.4408" - shown under the device name, matching where LibreNMS's own device page puts it.</summary>
    public string? SysDescr => string.IsNullOrWhiteSpace(_device?.SysDescr) ? null : _device.SysDescr;

    public bool HasSysDescr => SysDescr is not null;

    public DeviceState State => _isUnderMaintenance ? DeviceState.Maintenance : _device?.State ?? DeviceState.Down;

    public string StateText => State.ToDisplayString();

    public string Ip => Blank(_device?.Ip);

    /// <summary>
    /// What "Open in" (<see cref="OpenExternal"/>) targets: the device's own
    /// IP if LibreNMS has one, falling back to its hostname - never the
    /// display <see cref="Name"/>, which can be a sysName or custom display
    /// string that would not resolve as a network address at all.
    /// </summary>
    private string? OpenTarget => !string.IsNullOrWhiteSpace(_device?.Ip)
        ? _device!.Ip
        : (!string.IsNullOrWhiteSpace(_device?.Hostname) ? _device!.Hostname : null);

    /// <summary>Gates the three "Open in" commands - nothing to open in without a real address.</summary>
    public bool CanOpenExternally => OpenTarget is not null;

    public string Os => Blank(_device?.Os);

    public string Hardware => Blank(_device?.Hardware);

    public string Location => Blank(_device?.Location);

    /// <summary>Whether Location is a real value rather than the "-" placeholder - gates <see cref="ShowDevicesForLocationCommand"/>, since there is nothing useful to filter the Devices tab down to otherwise.</summary>
    public bool HasLocation => !string.IsNullOrWhiteSpace(_device?.Location);

    /// <summary>LibreNMS's own type values are lowercase ("network", "wireless", ...) - capitalised here to match how the Devices tab's type filter already displays them.</summary>
    public string Type => TitleCase(_device?.Type);

    public string UptimeText => _device is { State: DeviceState.Up } d ? FormatUptime(d.Uptime) : "-";

    // ------------------------------------------------------- additional details

    /// <summary>
    /// Inventory/identity fields LibreNMS's own device page shows but this
    /// app did not yet - each conditionally shown (see the matching HasX
    /// property below) rather than falling back to "-" like Ip/Os/Hardware
    /// above, since most devices leave several of these unset and a card
    /// full of dashes would be pure noise.
    /// </summary>
    public string? Serial => string.IsNullOrWhiteSpace(_device?.Serial) ? null : _device.Serial;

    public bool HasSerial => Serial is not null;

    public string? Contact => string.IsNullOrWhiteSpace(_device?.Contact) ? null : _device.Contact;

    public bool HasContact => Contact is not null;

    public string? SysObjectId => string.IsNullOrWhiteSpace(_device?.SysObjectId) ? null : _device.SysObjectId;

    public bool HasSysObjectId => SysObjectId is not null;

    /// <summary>Comma-separated hostnames, as LibreNMS itself stores them - most devices have none.</summary>
    public string? DependencyParentText => string.IsNullOrWhiteSpace(_device?.DependencyParentHostname) ? null : _device.DependencyParentHostname;

    public bool HasDependencyParent => DependencyParentText is not null;

    /// <summary>
    /// Elapsed time since LibreNMS added this device, e.g. "36d 4h ago" -
    /// same compact style as <see cref="UptimeText"/>/alert ages elsewhere in
    /// this app, not LibreNMS's own spelled-out "1 month ago" wording.
    /// Not converted from server time, matching how the event log and alert
    /// history timestamps in this same file are already treated - both are
    /// the same ambiguous MySQL datetime format this field also uses.
    /// </summary>
    public string? InsertedText => _device?.Inserted is { } t ? DurationFormat.Format(DateTime.Now - t) + " ago" : null;

    public bool HasInserted => InsertedText is not null;

    /// <summary>When LibreNMS last ran full discovery (not just a poll) against this device - see <see cref="InsertedText"/>'s remarks on formatting and timezone.</summary>
    public string? LastDiscoveredText => _device?.LastDiscovered is { } t ? DurationFormat.Format(DateTime.Now - t) + " ago" : null;

    public bool HasLastDiscovered => LastDiscoveredText is not null;

    /// <summary>Whether the Overview's "Technical" card has anything to show at all - it should not appear as an empty card for a device with none of Object ID/Serial/Depends-on. Location/Contact and Uptime/Device-added/Last-discovered have their own cards, but never hide entirely, since Location and Uptime always show something (even just "-").</summary>
    public bool HasTechnicalDetails => HasSysObjectId || HasSerial || HasDependencyParent;

    /// <summary>
    /// Closes this window and jumps to the Devices tab isolated down to this
    /// device's own location, discarding whatever filters were already set
    /// there - a location is a physical grouping devices actually share, so
    /// "show me the rest of what's here" is the useful action, not "add this
    /// to whatever I already had selected".
    /// </summary>
    private void ShowDevicesForLocation()
    {
        if (string.IsNullOrWhiteSpace(_device?.Location))
        {
            return;
        }

        _windows.ShowDevicesFilteredByLocation(_device.Location);
        _windows.CloseDeviceDetail(_deviceId);
    }

    /// <summary>
    /// Builds a "{scheme}://{OpenTarget}" URI and hands it to whatever the OS
    /// has registered for that scheme (see <see cref="IWindowService.OpenExternalTool"/>)
    /// - DashyNMS does not bundle a web/telnet/ssh client of its own.
    /// </summary>
    private void OpenExternal(string scheme)
    {
        if (OpenTarget is not { } target || !Uri.TryCreate($"{scheme}://{target}", UriKind.Absolute, out var uri))
        {
            return;
        }

        _windows.OpenExternalTool(uri);
    }

    /// <summary>True once the shared device monitor has actually reported on this device at least once.</summary>
    public bool HasLoaded => _device is not null;

    /// <summary>True until the first device poll lands, so the header can say so instead of showing blank fields.</summary>
    public bool IsLoadingDevice => !HasLoaded;

    public bool HasSensors => Sensors.Count > 0;

    /// <summary>True until the shared sensor monitor has reported on this device at least once - distinguishes "still loading" from "confirmed no sensors" below.</summary>
    public bool IsLoadingSensors => !_hasLoadedSensors;

    /// <summary>True once <see cref="SensorGroups"/> has something to show - false either for a device with no sensors at all, or one where <see cref="SensorSearchText"/> currently matches none.</summary>
    public bool HasVisibleSensorGroups => SensorGroups.Count > 0;

    /// <summary>Shown once loading has finished and the device genuinely has no sensors - never while still loading, and never just because the current search matched nothing.</summary>
    public bool ShowSensorsEmptyMessage => !IsLoadingSensors && !HasSensors;

    /// <summary>Shown once loaded, when the device has sensors but the current search matched none of them.</summary>
    public bool ShowSensorsNoMatchesMessage => !IsLoadingSensors && HasSensors && !HasVisibleSensorGroups;

    public int SensorWarningCount => Sensors.Count(s => s.Severity == AlertSeverity.Warning);

    public int SensorCriticalCount => Sensors.Count(s => s.Severity == AlertSeverity.Critical);

    /// <summary>"2 critical, 1 warning", or empty when nothing is out of range - a quick-glance summary for the Overview card.</summary>
    public string SensorAlertSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (SensorCriticalCount > 0) parts.Add($"{SensorCriticalCount} critical");
            if (SensorWarningCount > 0) parts.Add($"{SensorWarningCount} warning");
            return string.Join(", ", parts);
        }
    }

    public bool HasAlertHistory => AlertHistory.Count > 0;

    /// <summary>True until alert history has actually been fetched at least once - distinguishes "still loading" from "confirmed no history" below.</summary>
    public bool IsLoadingAlertHistory => !_hasLoadedAlertHistory;

    /// <summary>Shown once loading has finished and the device genuinely has no alert history.</summary>
    public bool ShowNoAlertHistoryMessage => !IsLoadingAlertHistory && !HasAlertHistory;

    public bool HasEventLog => EventLog.Count > 0;

    /// <summary>True until the event log has actually been fetched at least once - distinguishes "still loading" from "confirmed no entries" below.</summary>
    public bool IsLoadingEventLog => !_hasLoadedEventLog;

    /// <summary>True once <see cref="EventLogView"/> has something to show - false either for a device with no event log at all, or one where <see cref="EventLogSearchText"/> currently matches none.</summary>
    public bool HasVisibleEventLog => EventLogView.Cast<object>().Any();

    /// <summary>Shown once loading has finished and the device genuinely has no event log entries - never while still loading, and never just because the current search matched nothing.</summary>
    public bool ShowEventLogEmptyMessage => !IsLoadingEventLog && !HasEventLog;

    /// <summary>Shown once loaded, when the device has event log entries but the current search matched none of them.</summary>
    public bool ShowEventLogNoMatchesMessage => !IsLoadingEventLog && HasEventLog && !HasVisibleEventLog;

    /// <summary>
    /// True once ports have actually been fetched and the device reports at
    /// least one - many devices (UPS units, cameras, appliances) have no SNMP
    /// interfaces at all, so the Ports tab only shows up for devices that have them.
    /// </summary>
    public bool HasPorts => Ports.Count > 0;

    /// <summary>True until Ports has actually been fetched at least once - distinguishes "still loading" from "confirmed no ports" below.</summary>
    public bool IsLoadingPorts => !_hasLoadedPorts;

    /// <summary>
    /// Whether the sidebar's Ports item (and Overview's Ports card) should
    /// show: visible while still loading, so it does not pop into existence
    /// after the fact and shift everything below it, and once actually
    /// confirmed to have ports - hidden only once loading has finished and
    /// the device genuinely has none.
    /// </summary>
    public bool ShowPortsNav => IsLoadingPorts || HasPorts;

    /// <summary>True once <see cref="PortsView"/> has something to show - false either for a device with no ports at all, or one where <see cref="PortSearchText"/> currently matches none.</summary>
    public bool HasVisiblePorts => PortsView.Cast<object>().Any();

    /// <summary>Shown once loading has finished and the device genuinely has no ports - never while still loading, and never just because the current search matched nothing.</summary>
    public bool ShowPortsEmptyMessage => !IsLoadingPorts && !HasPorts;

    /// <summary>Shown once loaded, when the device has ports but the current search matched none of them.</summary>
    public bool ShowPortsNoMatchesMessage => !IsLoadingPorts && HasPorts && !HasVisiblePorts;

    public bool HasVlans => VlanEntries.Count > 0;

    /// <summary>True until VLANs has actually been fetched at least once - distinguishes "still loading" from "confirmed no VLANs" below.</summary>
    public bool IsLoadingVlans => !_hasLoadedVlans;

    /// <summary>Whether the sidebar's VLANs item should show - see <see cref="ShowPortsNav"/>'s remarks, which apply equally here.</summary>
    public bool ShowVlansNav => IsLoadingVlans || HasVlans;

    /// <summary>True once <see cref="VlansView"/> has something to show - false either for a device with no VLANs at all, or one where <see cref="VlanSearchText"/> currently matches none.</summary>
    public bool HasVisibleVlans => VlansView.Cast<object>().Any();

    /// <summary>Shown once loading has finished and the device genuinely has no VLANs - never while still loading, and never just because the current search matched nothing.</summary>
    public bool ShowVlansEmptyMessage => !IsLoadingVlans && !HasVlans;

    /// <summary>Shown once loaded, when the device has VLANs but the current search matched none of them.</summary>
    public bool ShowVlansNoMatchesMessage => !IsLoadingVlans && HasVlans && !HasVisibleVlans;

    /// <summary>Whether the sidebar's "Network" group has anything to show at all - it should not appear as an empty header for a device with none of these, but also should not disappear and reappear as each loads independently.</summary>
    public bool HasNetworkSection => ShowPortsNav || ShowVlansNav || ShowFdbNav || ShowArpNav;

    public int PortsUpCount => Ports.Count(p => p.IsUp);

    public int PortsDownCount => Ports.Count(p => !p.IsUp);

    /// <summary>
    /// True once CPU/memory/disk have actually been fetched and the device
    /// reports at least one of them - like <see cref="HasPorts"/>, plenty of
    /// devices (switches, PDUs, sensors-only appliances) expose none of these.
    /// </summary>
    public bool HasResources => Processors.Count > 0 || Mempools.Count > 0 || Storage.Count > 0;

    /// <summary>True until CPU/memory/disk have actually been fetched at least once - distinguishes "still loading" from "confirmed none of these" below.</summary>
    public bool IsLoadingResources => !_hasLoadedResources;

    /// <summary>Whether the sidebar's Resources item (and Overview's Resources card) should show - see <see cref="ShowPortsNav"/>'s remarks, which apply equally here.</summary>
    public bool ShowResourcesNav => IsLoadingResources || HasResources;

    /// <summary>Shown once loading has finished and the device genuinely reports none of CPU/memory/disk.</summary>
    public bool ShowResourcesEmptyMessage => !IsLoadingResources && !HasResources;

    public bool HasProcessors => Processors.Count > 0;

    public bool HasMempools => Mempools.Count > 0;

    public bool HasStorage => Storage.Count > 0;

    /// <summary>Average usage across every CPU/core LibreNMS reports, or null when the device has none.</summary>
    public double? CpuUsagePercent => Processors.Count > 0 ? Processors.Average(p => p.UsagePercent) : null;

    /// <summary>
    /// The pool named "Physical memory" if there is one (the common case on
    /// Linux/UCD-SNMP hosts) - otherwise whichever pool is fullest, since an
    /// arbitrary vendor's naming cannot be relied on and the fullest pool is
    /// the one worth surfacing at a glance regardless.
    /// </summary>
    public double? MemoryUsagePercent => Mempools.Count == 0
        ? null
        : (Mempools.FirstOrDefault(m => m.Description.Contains("Physical", StringComparison.OrdinalIgnoreCase))
            ?? Mempools.OrderByDescending(m => m.UsagePercent).First()).UsagePercent;

    /// <summary>The fullest volume, since that is the one worth surfacing at a glance regardless of how many others are healthy.</summary>
    public double? DiskUsagePercent => Storage.Count > 0 ? Storage.Max(s => s.UsagePercent) : null;

    /// <summary>"24% CPU, 63% RAM, 92% disk" for the Overview card - only the metrics this device actually reports.</summary>
    public string ResourceSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (CpuUsagePercent is { } cpu) parts.Add($"{cpu:0}% CPU");
            if (MemoryUsagePercent is { } memory) parts.Add($"{memory:0}% RAM");
            if (DiskUsagePercent is { } disk) parts.Add($"{disk:0}% disk");
            return string.Join(", ", parts);
        }
    }

    /// <summary>True once availability has actually been fetched - hides the Overview card rather than showing dashes until then.</summary>
    public bool HasAvailability => _availability1Day is not null;

    public string Availability1DayText => FormatPercent(_availability1Day);

    public string Availability7DayText => FormatPercent(_availability7Day);

    public string Availability30DayText => FormatPercent(_availability30Day);

    public string Availability1YearText => FormatPercent(_availability1Year);

    public bool HasOutages => Outages.Count > 0;

    /// <summary>True once the MAC address table has actually been fetched and the device reports at least one entry - not every device is a switch.</summary>
    public bool HasFdbEntries => FdbEntries.Count > 0;

    /// <summary>True until the FDB has actually been fetched at least once - distinguishes "still loading" from "confirmed no entries" below.</summary>
    public bool IsLoadingFdb => !_hasLoadedFdb;

    /// <summary>Whether the sidebar's FDB item should show - see <see cref="ShowPortsNav"/>'s remarks, which apply equally here.</summary>
    public bool ShowFdbNav => IsLoadingFdb || HasFdbEntries;

    /// <summary>True once <see cref="FdbView"/> has something to show - false either for a device with no FDB entries at all, or one where <see cref="FdbSearchText"/> currently matches none.</summary>
    public bool HasVisibleFdbEntries => FdbView.Cast<object>().Any();

    /// <summary>Shown once loading has finished and the device genuinely has no FDB entries - never while still loading, and never just because the current search matched nothing.</summary>
    public bool ShowFdbEmptyMessage => !IsLoadingFdb && !HasFdbEntries;

    /// <summary>Shown once loaded, when the device has FDB entries but the current search matched none of them.</summary>
    public bool ShowFdbNoMatchesMessage => !IsLoadingFdb && HasFdbEntries && !HasVisibleFdbEntries;

    /// <summary>True once the ARP table has actually been fetched and the device reports at least one entry - not every device does IP routing.</summary>
    public bool HasArpEntries => ArpEntries.Count > 0;

    /// <summary>True until the ARP table has actually been fetched at least once - distinguishes "still loading" from "confirmed no entries" below.</summary>
    public bool IsLoadingArp => !_hasLoadedArp;

    /// <summary>Whether the sidebar's ARP item should show - see <see cref="ShowPortsNav"/>'s remarks, which apply equally here.</summary>
    public bool ShowArpNav => IsLoadingArp || HasArpEntries;

    /// <summary>True once <see cref="ArpView"/> has something to show - false either for a device with no ARP entries at all, or one where <see cref="ArpSearchText"/> currently matches none.</summary>
    public bool HasVisibleArpEntries => ArpView.Cast<object>().Any();

    /// <summary>Shown once loading has finished and the device genuinely has no ARP entries - never while still loading, and never just because the current search matched nothing.</summary>
    public bool ShowArpEmptyMessage => !IsLoadingArp && !HasArpEntries;

    /// <summary>Shown once loaded, when the device has ARP entries but the current search matched none of them.</summary>
    public bool ShowArpNoMatchesMessage => !IsLoadingArp && HasArpEntries && !HasVisibleArpEntries;

    public bool HasActiveAlerts => ActiveAlerts.Count > 0;

    /// <summary>True until the shared alert monitor has reported on this device at least once - distinguishes "still loading" from "confirmed no active alerts" below.</summary>
    public bool IsLoadingActiveAlerts => !_hasLoadedActiveAlerts;

    /// <summary>Shown once loading has finished and the device genuinely has no active alerts.</summary>
    public bool ShowNoActiveAlertsMessage => !IsLoadingActiveAlerts && !HasActiveAlerts;

    public int ActiveCriticalCount => ActiveAlerts.Count(a => a.Severity == AlertSeverity.Critical);

    public int ActiveWarningCount => ActiveAlerts.Count(a => a.Severity == AlertSeverity.Warning);

    /// <summary>"2 critical, 1 warning", or empty - a quick-glance summary next to the Overview's Active alerts header.</summary>
    public string ActiveAlertSummaryText
    {
        get
        {
            var parts = new List<string>();
            if (ActiveCriticalCount > 0) parts.Add($"{ActiveCriticalCount} critical");
            if (ActiveWarningCount > 0) parts.Add($"{ActiveWarningCount} warning");
            return string.Join(", ", parts);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// True while a rediscover request is in flight - separate from
    /// <see cref="IsBusy"/>, which tracks the section-load spinners, since
    /// rediscovering does not touch any of that data itself (LibreNMS applies
    /// the result on its own schedule, not synchronously in the response).
    /// </summary>
    public bool IsRediscovering
    {
        get => _isRediscovering;
        private set
        {
            if (SetProperty(ref _isRediscovering, value))
            {
                RediscoverCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True while a delete request is in flight - see <see cref="DeleteAsync"/>.</summary>
    public bool IsDeleting
    {
        get => _isDeleting;
        private set
        {
            if (SetProperty(ref _isDeleting, value))
            {
                DeleteCommand.RaiseCanExecuteChanged();
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

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    // --------------------------------------------------------------- handlers

    private void OnDevicePolled(object? sender, DevicePollResult result) => _dispatcher.InvokeAsync(() =>
    {
        if (!result.Succeeded)
        {
            return;
        }

        var device = result.Devices.FirstOrDefault(d => d.DeviceId == _deviceId);
        if (device is null)
        {
            // Removed from LibreNMS since the window opened - leave the last
            // known detail showing rather than blanking the window out.
            return;
        }

        _device = device;
        _isUnderMaintenance = result.DeviceIdsUnderMaintenance.Contains(_deviceId);
        RaiseDeviceChanged();
    });

    private void OnSensorPolled(object? sender, SensorPollResult result) => _dispatcher.InvokeAsync(() =>
    {
        // Flips once, on the very first poll to come back - success or not -
        // regardless of whether it actually carried anything for this
        // device, so IsLoadingSensors stops being true even if this device
        // turns out to report none at all.
        if (!_hasLoadedSensors)
        {
            _hasLoadedSensors = true;
            OnPropertyChanged(nameof(IsLoadingSensors));
            OnPropertyChanged(nameof(ShowSensorsEmptyMessage));
            OnPropertyChanged(nameof(ShowSensorsNoMatchesMessage));
        }

        if (result.Succeeded)
        {
            ApplySensors(result.Sensors);
        }
    });

    /// <summary>
    /// AlertMonitor polls the whole fleet, same as everywhere else in the app
    /// that needs alerts - there is no per-device filter on the server side -
    /// so this just picks out the rows for this one device from what it
    /// already fetched, at no extra API cost.
    /// </summary>
    private void OnAlertsPolled(object? sender, AlertPollResult result) => _dispatcher.InvokeAsync(() =>
    {
        // See OnSensorPolled's remarks - flips once regardless of outcome.
        if (!_hasLoadedActiveAlerts)
        {
            _hasLoadedActiveAlerts = true;
            OnPropertyChanged(nameof(IsLoadingActiveAlerts));
            OnPropertyChanged(nameof(ShowNoActiveAlertsMessage));
        }

        if (result.Succeeded)
        {
            ApplyActiveAlerts(result.Alerts);
        }
    });

    private void ApplyActiveAlerts(IReadOnlyList<Alert> fleet)
    {
        var serverTimestampsAreUtc = _settings.Current.ServerTimestampsAreUtc;

        var mine = fleet
            .Where(a => a.DeviceId == _deviceId && a.State is AlertState.Active or AlertState.Acknowledged)
            .OrderByDescending(a => a.Severity.SortRank())
            .ThenByDescending(a => a.Timestamp)
            .Select(a => new ActiveAlertItemViewModel(a, serverTimestampsAreUtc))
            .ToList();

        ActiveAlerts.Clear();
        foreach (var alert in mine)
        {
            ActiveAlerts.Add(alert);
        }

        OnPropertyChanged(nameof(HasActiveAlerts));
        OnPropertyChanged(nameof(ShowNoActiveAlertsMessage));
        OnPropertyChanged(nameof(ActiveCriticalCount));
        OnPropertyChanged(nameof(ActiveWarningCount));
        OnPropertyChanged(nameof(ActiveAlertSummaryText));
    }

    private void ApplySensors(IReadOnlyList<Sensor> fleet)
    {
        var settings = _settings.Current;
        var connection = _session.Connection;
        var deviceName = Name;

        var mine = fleet
            .Where(s => s.DeviceId == _deviceId)
            .OrderBy(s => s.SensorClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var incoming = mine.Select(s => s.SensorId).ToHashSet();

        for (var i = Sensors.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Sensors[i].SensorId))
            {
                _sensorIndex.Remove(Sensors[i].SensorId);
                Sensors.RemoveAt(i);
            }
        }

        for (var target = 0; target < mine.Count; target++)
        {
            var sensor = mine[target];

            // dBm/signal/temperature/fan speed have an app-configured
            // fallback (see SensorCategoryRegistry) for whichever bound the
            // sensor itself leaves unconfigured, and respect the Settings
            // toggle to always prefer that fallback; every other class has
            // only the sensor's own limits to go on.
            var entry = SensorCategoryRegistry.Resolve(sensor.SensorClass);
            var evaluator = entry?.Thresholds(settings, sensor) ?? new SensorLimitThresholdEvaluator(sensor);
            var unit = entry?.UnitSuffix ?? SensorUnitDisplay.Resolve(sensor.SensorClass);

            if (_sensorIndex.TryGetValue(sensor.SensorId, out var existing))
            {
                existing.Update(sensor, deviceName, connection, evaluator);

                var currentIndex = Sensors.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != target && target < Sensors.Count)
                {
                    Sensors.Move(currentIndex, target);
                }
            }
            else
            {
                var item = new SensorItemViewModel(sensor, deviceName, connection, evaluator, unit);
                _sensorIndex[sensor.SensorId] = item;
                Sensors.Insert(Math.Min(target, Sensors.Count), item);
            }
        }

        RebuildSensorGroupsIfChanged();

        OnPropertyChanged(nameof(HasSensors));
        OnPropertyChanged(nameof(ShowSensorsEmptyMessage));
        OnPropertyChanged(nameof(ShowSensorsNoMatchesMessage));
        OnPropertyChanged(nameof(SensorWarningCount));
        OnPropertyChanged(nameof(SensorCriticalCount));
        OnPropertyChanged(nameof(SensorAlertSummaryText));
    }

    /// <summary>Rebuilds <see cref="SensorGroups"/> only when the grouping actually differs from what is already on screen - see <see cref="RebuildSensorGroups"/>.</summary>
    private void RebuildSensorGroupsIfChanged() => RebuildSensorGroups(force: false);

    /// <summary>
    /// Rebuilds <see cref="SensorGroups"/> from whichever sensors currently
    /// match <see cref="SensorSearchText"/> (all of them, when it is empty).
    /// Unless <paramref name="force"/> is set, this is skipped when the
    /// result would not actually differ from what is already on screen -
    /// which sensors a device has barely ever changes, so the common case -
    /// every poll - is a no-op, and the rows keep their view models and
    /// update their values in place rather than the whole list being torn
    /// down and rebuilt (which would lose the scroll position every 30
    /// seconds). A search text change always forces a rebuild, since the set
    /// of matching sensors just changed by definition.
    /// </summary>
    private void RebuildSensorGroups(bool force)
    {
        var term = SensorSearchText.Trim();
        var matching = term.Length == 0
            ? Sensors
            : (IEnumerable<SensorItemViewModel>)Sensors.Where(s => s.Matches(term));

        var grouped = matching
            .GroupBy(s => s.GroupKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SensorGroupViewModel(
                g.Key,
                g.OrderBy(s => s.Description, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        if (!force && MatchesCurrentGroups(grouped))
        {
            return;
        }

        SensorGroups.Clear();
        foreach (var group in grouped)
        {
            SensorGroups.Add(group);
        }

        OnPropertyChanged(nameof(HasVisibleSensorGroups));
    }

    private bool MatchesCurrentGroups(IReadOnlyList<SensorGroupViewModel> candidate)
    {
        if (candidate.Count != SensorGroups.Count)
        {
            return false;
        }

        for (var i = 0; i < candidate.Count; i++)
        {
            var left = candidate[i];
            var right = SensorGroups[i];

            if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
                || left.Sensors.Count != right.Sensors.Count)
            {
                return false;
            }

            for (var j = 0; j < left.Sensors.Count; j++)
            {
                if (left.Sensors[j].SensorId != right.Sensors[j].SensorId)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task LoadAlertHistoryAsync()
    {
        IsBusy = true;

        try
        {
            var entries = await _client.Logs.ListAlertLogAsync(_deviceId, 30, _loadCts.Token).ConfigureAwait(true);
            var ruleIds = entries.Select(e => e.RuleId).Distinct().ToList();

            // The rule tells us its name and which columns its condition
            // actually tests; the log entry's own "details" blob has the
            // values - same lookup the main Alerts tab uses for its fault
            // view, and the same shared cache, so a rule already seen there
            // (or by another device's history) costs nothing here.
            var rulesByRule = new Dictionary<int, AlertRule?>();
            var fieldsByRule = new Dictionary<int, IReadOnlySet<string>>();
            foreach (var ruleId in ruleIds)
            {
                rulesByRule[ruleId] = await _ruleFields.GetRuleAsync(ruleId, _loadCts.Token).ConfigureAwait(true);
                fieldsByRule[ruleId] = await _ruleFields.GetConditionFieldsAsync(ruleId, _loadCts.Token).ConfigureAwait(true);
            }

            AlertHistory.Clear();
            foreach (var entry in entries)
            {
                rulesByRule.TryGetValue(entry.RuleId, out var rule);
                fieldsByRule.TryGetValue(entry.RuleId, out var fields);
                var detail = AlertFaultParser.Parse(entry, fields);
                AlertHistory.Add(new AlertLogItemViewModel(entry, rule, detail));
            }

            OnPropertyChanged(nameof(HasAlertHistory));
            ErrorMessage = null;
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up
            // rather than log a spurious "could not load" warning.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load alert history for device {DeviceId}", _deviceId);
            ErrorMessage = ex.ToUserMessage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load alert history for device {DeviceId}", _deviceId);
            ErrorMessage = "Could not load alert history.";
        }
        finally
        {
            IsBusy = false;
            _hasLoadedAlertHistory = true;
            OnPropertyChanged(nameof(IsLoadingAlertHistory));
            OnPropertyChanged(nameof(ShowNoAlertHistoryMessage));
        }
    }

    /// <summary>
    /// LibreNMS has no fleet-wide ports endpoint, only per device, so unlike
    /// sensors/devices/alerts this is fetched fresh rather than filtered from
    /// a shared poller. A failure is logged and quietly leaves the list empty
    /// - many devices genuinely have no SNMP interfaces at all, and treating
    /// that the same as an error would raise a false alarm on every one of them.
    /// </summary>
    private async Task LoadPortsAsync()
    {
        try
        {
            var ports = await _client.Ports.ListForDeviceAsync(_deviceId, _loadCts.Token).ConfigureAwait(true);

            // Neighbours and IP addresses are fetched independently and each
            // tolerate their own failure - a problem with one endpoint
            // (possibly unsupported on an older LibreNMS version) should not
            // take the ports list down with it.
            var linksTask = TryLoadLinksAsync();
            var addressesTask = TryLoadIpAddressesAsync();
            await Task.WhenAll(linksTask, addressesTask).ConfigureAwait(true);

            var linksByPort = linksTask.Result
                .Where(l => l.LocalPortId > 0)
                .GroupBy(l => l.LocalPortId)
                .ToDictionary(g => g.Key, g => g.First());

            var addressesByPort = addressesTask.Result
                .GroupBy(a => a.PortId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<DeviceIpAddress>)g.ToList());

            _portNamesByPortId.Clear();
            var portItems = new List<PortItemViewModel>();
            foreach (var port in ports.OrderBy(p => p.IfIndex ?? int.MaxValue))
            {
                linksByPort.TryGetValue(port.PortId, out var link);
                addressesByPort.TryGetValue(port.PortId, out var addresses);
                portItems.Add(new PortItemViewModel(port, link, addresses ?? Array.Empty<DeviceIpAddress>(), _windows));
                _portNamesByPortId[port.PortId] = port.DisplayName;
            }

            Ports.ReplaceAll(portItems);

            // FDB/ARP may well have already loaded (this call fetches
            // neighbours and IP addresses too, so it is not reliably the
            // fastest of the three) with rows falling back to "Port {id}" -
            // now that names are known, tell those already-created rows to
            // re-read PortText instead of leaving the fallback showing for
            // the rest of the session.
            foreach (var entry in FdbEntries)
            {
                entry.RefreshPortName();
            }

            foreach (var entry in ArpEntries)
            {
                entry.RefreshPortName();
            }

            // Same reasoning, for the VLANs tab's own per-VLAN port list -
            // it queries this same Ports collection live, but a row built
            // before this completed needs telling to re-read it.
            foreach (var vlan in VlanEntries)
            {
                vlan.RefreshPorts();
            }

            OnPropertyChanged(nameof(HasPorts));
            OnPropertyChanged(nameof(HasVisiblePorts));
            OnPropertyChanged(nameof(ShowPortsEmptyMessage));
            OnPropertyChanged(nameof(ShowPortsNoMatchesMessage));
            OnPropertyChanged(nameof(PortsUpCount));
            OnPropertyChanged(nameof(PortsDownCount));
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            // Warning, not Debug: a genuine fetch failure (bad request, timeout,
            // server error) should be visible in the log, not indistinguishable
            // from the ordinary case of a device that simply has no ports.
            // ServerMessage (LibreNMS's own explanation, e.g. which column name
            // it rejected) is logged explicitly since ex.Message alone is just
            // the generic "HTTP 400" wrapper - see LibreNmsApiException.
            _logger.LogWarning(ex, "Could not load ports for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load ports for device {DeviceId}", _deviceId);
        }
        finally
        {
            _hasLoadedPorts = true;
            OnPropertyChanged(nameof(IsLoadingPorts));
            OnPropertyChanged(nameof(ShowPortsNav));
            OnPropertyChanged(nameof(ShowPortsEmptyMessage));
            OnPropertyChanged(nameof(HasNetworkSection));
        }
    }

    private async Task<IReadOnlyList<NetworkLink>> TryLoadLinksAsync()
    {
        try
        {
            return await _client.Links.ListForDeviceAsync(_deviceId, _loadCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Propagate rather than swallow-and-return-empty like the
            // catches below - the caller's own Task.WhenAll should see this
            // as cancelled, not as "this device just has no neighbours".
            throw;
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load neighbours for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
            return Array.Empty<NetworkLink>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load neighbours for device {DeviceId}", _deviceId);
            return Array.Empty<NetworkLink>();
        }
    }

    private async Task<IReadOnlyList<DeviceIpAddress>> TryLoadIpAddressesAsync()
    {
        try
        {
            return await _client.Ports.ListIpAddressesAsync(_deviceId, _loadCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load IP addresses for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
            return Array.Empty<DeviceIpAddress>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load IP addresses for device {DeviceId}", _deviceId);
            return Array.Empty<DeviceIpAddress>();
        }
    }

    /// <summary>
    /// Every VLAN configured on this device - its own tab, and also what
    /// <see cref="FdbEntry.VlanId"/> resolves against on the FDB tab (see
    /// <see cref="_vlansById"/>). The VLANs endpoint has no per-device filter
    /// that also returns the internal id FdbEntry.VlanId needs (see
    /// <see cref="IVlansApi"/>'s remarks), so it comes back for the whole
    /// fleet and is filtered down here - fetched independently, same as
    /// ports/resources, so a problem here cannot take another tab down with it.
    /// </summary>
    private async Task LoadVlansAsync()
    {
        try
        {
            var vlans = await _client.Vlans.ListAsync(_loadCts.Token).ConfigureAwait(true);

            var mine = vlans
                .Where(v => v.DeviceId == _deviceId)
                .OrderBy(v => v.VlanNumber)
                .ToList();

            // Mutated in place, not reassigned: FdbItemViewModel rows already
            // built hold this same dictionary reference (see LoadFdbAsync),
            // and RefreshVlan below only helps if their next read of it sees
            // these updated values rather than whatever a fresh dictionary
            // object would have held.
            _vlansById.Clear();
            foreach (var vlan in mine)
            {
                _vlansById[vlan.VlanId] = vlan;
            }

            VlanEntries.ReplaceAll(mine.Select(vlan => new VlanItemViewModel(vlan, Ports)));

            // FDB rows built before this finished resolved against whatever
            // was in _vlansById at the time (likely nothing) - tell them to
            // re-check now that it is populated.
            foreach (var entry in FdbEntries)
            {
                entry.RefreshVlan();
            }

            OnPropertyChanged(nameof(HasVlans));
            OnPropertyChanged(nameof(HasVisibleVlans));
            OnPropertyChanged(nameof(ShowVlansEmptyMessage));
            OnPropertyChanged(nameof(ShowVlansNoMatchesMessage));
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load VLANs for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load VLANs for device {DeviceId}", _deviceId);
        }
        finally
        {
            _hasLoadedVlans = true;
            OnPropertyChanged(nameof(IsLoadingVlans));
            OnPropertyChanged(nameof(ShowVlansNav));
            OnPropertyChanged(nameof(ShowVlansEmptyMessage));
            OnPropertyChanged(nameof(HasNetworkSection));
        }
    }

    /// <summary>
    /// The MAC address table. Fetched independently, same as ports/resources,
    /// so a problem here cannot take another tab down with it - most devices
    /// (anything that is not a switch) report none of these at all, which is
    /// not an error, just an empty result.
    /// </summary>
    private async Task LoadFdbAsync()
    {
        try
        {
            var entries = await _client.Fdb.ListForDeviceAsync(_deviceId, _loadCts.Token).ConfigureAwait(true);

            FdbEntries.ReplaceAll(entries
                .OrderBy(e => e.MacAddress, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new FdbItemViewModel(entry, _portNamesByPortId, _vlansById)));

            OnPropertyChanged(nameof(HasFdbEntries));
            OnPropertyChanged(nameof(HasVisibleFdbEntries));
            OnPropertyChanged(nameof(ShowFdbEmptyMessage));
            OnPropertyChanged(nameof(ShowFdbNoMatchesMessage));
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load the FDB for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the FDB for device {DeviceId}", _deviceId);
        }
        finally
        {
            _hasLoadedFdb = true;
            OnPropertyChanged(nameof(IsLoadingFdb));
            OnPropertyChanged(nameof(ShowFdbNav));
            OnPropertyChanged(nameof(ShowFdbEmptyMessage));
            OnPropertyChanged(nameof(HasNetworkSection));
        }
    }

    /// <summary>The ARP table. Fetched independently - see <see cref="LoadFdbAsync"/>'s remarks, which apply equally here.</summary>
    private async Task LoadArpAsync()
    {
        try
        {
            var entries = await _client.Arp.ListForDeviceAsync(_deviceId, _loadCts.Token).ConfigureAwait(true);

            ArpEntries.ReplaceAll(entries
                .OrderBy(e => e.Ipv4Address, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new ArpItemViewModel(entry, _portNamesByPortId)));

            OnPropertyChanged(nameof(HasArpEntries));
            OnPropertyChanged(nameof(HasVisibleArpEntries));
            OnPropertyChanged(nameof(ShowArpEmptyMessage));
            OnPropertyChanged(nameof(ShowArpNoMatchesMessage));
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load the ARP table for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the ARP table for device {DeviceId}", _deviceId);
        }
        finally
        {
            _hasLoadedArp = true;
            OnPropertyChanged(nameof(IsLoadingArp));
            OnPropertyChanged(nameof(ShowArpNav));
            OnPropertyChanged(nameof(ShowArpEmptyMessage));
            OnPropertyChanged(nameof(HasNetworkSection));
        }
    }

    /// <summary>
    /// CPU/memory/disk usage. Fetched independently, same as ports/neighbours,
    /// so a problem here cannot take another tab down with it - most devices
    /// (switches, PDUs, anything SNMP-only) report none of these at all, which
    /// is not an error, just an empty result.
    /// </summary>
    private async Task LoadResourcesAsync()
    {
        try
        {
            var processorsTask = _client.Health.ListProcessorsAsync(_deviceId, _loadCts.Token);
            var mempoolsTask = _client.Health.ListMempoolsAsync(_deviceId, _loadCts.Token);
            var storageTask = _client.Health.ListStorageAsync(_deviceId, _loadCts.Token);
            await Task.WhenAll(processorsTask, mempoolsTask, storageTask).ConfigureAwait(true);

            Processors.Clear();
            foreach (var processor in processorsTask.Result.OrderBy(p => p.Description, StringComparer.OrdinalIgnoreCase))
            {
                Processors.Add(new ProcessorItemViewModel(processor));
            }

            Mempools.Clear();
            foreach (var mempool in mempoolsTask.Result.OrderBy(m => m.Description, StringComparer.OrdinalIgnoreCase))
            {
                Mempools.Add(new MempoolItemViewModel(mempool));
            }

            Storage.Clear();
            foreach (var volume in storageTask.Result.OrderBy(s => s.Description, StringComparer.OrdinalIgnoreCase))
            {
                Storage.Add(new StorageItemViewModel(volume));
            }

            OnPropertyChanged(nameof(HasResources));
            OnPropertyChanged(nameof(HasProcessors));
            OnPropertyChanged(nameof(HasMempools));
            OnPropertyChanged(nameof(HasStorage));
            OnPropertyChanged(nameof(CpuUsagePercent));
            OnPropertyChanged(nameof(MemoryUsagePercent));
            OnPropertyChanged(nameof(DiskUsagePercent));
            OnPropertyChanged(nameof(ResourceSummaryText));
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load resources for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load resources for device {DeviceId}", _deviceId);
        }
        finally
        {
            _hasLoadedResources = true;
            OnPropertyChanged(nameof(IsLoadingResources));
            OnPropertyChanged(nameof(ShowResourcesNav));
            OnPropertyChanged(nameof(ShowResourcesEmptyMessage));
        }
    }

    /// <summary>
    /// Uptime percentage (1 day/7 day/30 day/1 year) and downtime history.
    /// Fetched independently, same as ports/resources, so a problem here
    /// cannot take another tab down with it.
    /// </summary>
    private async Task LoadAvailabilityAsync()
    {
        try
        {
            var availabilityTask = _client.Devices.GetAvailabilityAsync(_deviceId, _loadCts.Token);
            var outagesTask = _client.Devices.GetOutagesAsync(_deviceId, _loadCts.Token);
            await Task.WhenAll(availabilityTask, outagesTask).ConfigureAwait(true);

            // Identified by duration rather than array position - LibreNMS's
            // own ordering is not worth trusting blindly, and this is cheap
            // either way since there are only ever four of them.
            double? PercentFor(long durationSeconds) => availabilityTask.Result
                .FirstOrDefault(w => w.DurationSeconds == durationSeconds)?.Percent;

            _availability1Day = PercentFor(86_400);
            _availability7Day = PercentFor(604_800);
            _availability30Day = PercentFor(2_592_000);
            _availability1Year = PercentFor(31_536_000);

            Outages.Clear();
            foreach (var outage in outagesTask.Result
                .OrderByDescending(o => o.GoingDown)
                .Take(MaxOutagesShown))
            {
                Outages.Add(new OutageItemViewModel(outage));
            }

            AvailabilityTimeline.Clear();
            foreach (var day in BuildAvailabilityTimeline(outagesTask.Result))
            {
                AvailabilityTimeline.Add(day);
            }

            OnPropertyChanged(nameof(HasAvailability));
            OnPropertyChanged(nameof(Availability1DayText));
            OnPropertyChanged(nameof(Availability7DayText));
            OnPropertyChanged(nameof(Availability30DayText));
            OnPropertyChanged(nameof(Availability1YearText));
            OnPropertyChanged(nameof(HasOutages));
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load availability for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load availability for device {DeviceId}", _deviceId);
        }
    }

    /// <summary>
    /// Every LibreNMS device group this device belongs to. Fetched
    /// independently, same as ports/resources, so a problem here cannot take
    /// another section down with it - most devices are in no group at all,
    /// which is not an error, just an empty result.
    /// </summary>
    private async Task LoadDeviceGroupsAsync()
    {
        try
        {
            var groups = await _client.DeviceGroups.ListForDeviceAsync(_deviceId, _loadCts.Token).ConfigureAwait(true);

            DeviceGroups.Clear();
            foreach (var group in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                DeviceGroups.Add(new DeviceGroupItemViewModel(group, ShowDevicesForGroup));
            }

            OnPropertyChanged(nameof(HasDeviceGroups));
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load device groups for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load device groups for device {DeviceId}", _deviceId);
        }
    }

    /// <summary>
    /// Closes this window and jumps to the Devices tab isolated down to one
    /// of this device's own groups, discarding whatever filters were already
    /// set there - see <see cref="ShowDevicesForLocation"/>'s remarks, which
    /// apply equally here.
    /// </summary>
    private void ShowDevicesForGroup(string groupName)
    {
        _windows.ShowDevicesFilteredByGroup(groupName);
        _windows.CloseDeviceDetail(_deviceId);
    }

    private static string FormatPercent(double? percent) =>
        percent is { } value ? value.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "-";

    /// <summary>
    /// Buckets every outage into local calendar days over the trailing
    /// <see cref="AvailabilityTimelineDays"/> window, oldest first, summing
    /// however much of each day fell inside a down period (an outage can span
    /// midnight, or several days). An outage still ongoing (<see cref="DeviceOutage.UpAgain"/>
    /// null) is treated as down through to now.
    /// </summary>
    private static List<OutageDayViewModel> BuildAvailabilityTimeline(IReadOnlyList<DeviceOutage> outages)
    {
        var today = DateTime.Today;
        var days = new List<OutageDayViewModel>(AvailabilityTimelineDays);

        for (var offset = AvailabilityTimelineDays - 1; offset >= 0; offset--)
        {
            var dayStart = today.AddDays(-offset);
            var dayEnd = dayStart.AddDays(1);
            double downSeconds = 0;

            foreach (var outage in outages)
            {
                if (outage.GoingDown is not { } start)
                {
                    continue;
                }

                var localStart = start.ToLocalTime();
                var localEnd = (outage.UpAgain ?? DateTime.UtcNow).ToLocalTime();

                var overlapStart = localStart > dayStart ? localStart : dayStart;
                var overlapEnd = localEnd < dayEnd ? localEnd : dayEnd;

                if (overlapEnd > overlapStart)
                {
                    downSeconds += (overlapEnd - overlapStart).TotalSeconds;
                }
            }

            days.Add(new OutageDayViewModel(DateOnly.FromDateTime(dayStart), downSeconds));
        }

        return days;
    }

    /// <summary>
    /// LibreNMS's general audit trail for the device (config changes, up/down
    /// transitions, polling events, ...) - distinct from the alert log, which
    /// is only what tripped an alert rule. Fetched independently, same as
    /// ports/neighbours, so a problem here cannot take another tab down with it.
    /// Always fetches from scratch at <see cref="EventLogPageSize"/> - see
    /// <see cref="LoadMoreEventLogAsync"/> for how "load more" widens that.
    /// </summary>
    private async Task LoadEventLogAsync()
    {
        _eventLogLimit = EventLogPageSize;

        try
        {
            var entries = await _client.Logs.ListEventLogAsync(_deviceId, _eventLogLimit, _loadCts.Token).ConfigureAwait(true);

            _loadedEventLogIds.Clear();
            var eventLogItems = new List<EventLogItemViewModel>();
            foreach (var entry in entries)
            {
                eventLogItems.Add(new EventLogItemViewModel(entry));
                _loadedEventLogIds.Add(entry.Id);
            }

            EventLog.ReplaceAll(eventLogItems);

            HasMoreEventLog = entries.Count >= _eventLogLimit;

            OnPropertyChanged(nameof(HasEventLog));
            OnPropertyChanged(nameof(HasVisibleEventLog));
            OnPropertyChanged(nameof(ShowEventLogEmptyMessage));
            OnPropertyChanged(nameof(ShowEventLogNoMatchesMessage));
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load event log for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load event log for device {DeviceId}", _deviceId);
        }
        finally
        {
            _hasLoadedEventLog = true;
            OnPropertyChanged(nameof(IsLoadingEventLog));
            OnPropertyChanged(nameof(ShowEventLogEmptyMessage));
        }
    }

    /// <summary>
    /// LibreNMS's eventlog endpoint has no working offset parameter - a
    /// "start" query parameter was tried and confirmed (against a real
    /// server, via logging) to have no effect, always returning the same top
    /// entries regardless. "Load more" therefore re-issues the same query
    /// with a bigger <see cref="_eventLogLimit"/> - the server always returns
    /// the newest N, so a bigger N is the existing entries plus more older
    /// ones tacked on the end - and only the new tail (by id, in case that
    /// assumption ever breaks) is appended, so the grid does not visibly
    /// rebuild from scratch.
    /// </summary>
    private async Task LoadMoreEventLogAsync()
    {
        IsLoadingMoreEventLog = true;
        var newLimit = _eventLogLimit + EventLogPageSize;

        try
        {
            var entries = await _client.Logs.ListEventLogAsync(_deviceId, newLimit, _loadCts.Token).ConfigureAwait(true);

            var added = 0;
            foreach (var entry in entries)
            {
                if (_loadedEventLogIds.Add(entry.Id))
                {
                    EventLog.Add(new EventLogItemViewModel(entry));
                    added++;
                }
            }

            _eventLogLimit = newLimit;
            HasMoreEventLog = added > 0 && entries.Count >= newLimit;
            OnPropertyChanged(nameof(HasEventLog));
            OnPropertyChanged(nameof(HasVisibleEventLog));
            OnPropertyChanged(nameof(ShowEventLogNoMatchesMessage));
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
            // Unlike the catches below, cancellation doesn't mean "no more
            // results exist", so HasMoreEventLog is deliberately left as-is.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load more event log entries for device {DeviceId}: {ServerMessage}", _deviceId, ex.ServerMessage);
            HasMoreEventLog = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load more event log entries for device {DeviceId}", _deviceId);
            HasMoreEventLog = false;
        }
        finally
        {
            IsLoadingMoreEventLog = false;
        }
    }

    private bool FilterEventLogEntry(object item)
    {
        if (item is not EventLogItemViewModel entry)
        {
            return false;
        }

        var term = EventLogSearchText;
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        return entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.TypeText.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.Username.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterPortEntry(object item) =>
        item is PortItemViewModel entry
        && (string.IsNullOrWhiteSpace(PortSearchText) || entry.Matches(PortSearchText.Trim()));

    private bool FilterVlanEntry(object item) =>
        item is VlanItemViewModel entry
        && (string.IsNullOrWhiteSpace(VlanSearchText) || entry.Matches(VlanSearchText.Trim()));

    private bool FilterFdbEntry(object item) =>
        item is FdbItemViewModel entry
        && (string.IsNullOrWhiteSpace(FdbSearchText) || entry.Matches(FdbSearchText.Trim()));

    private bool FilterArpEntry(object item) =>
        item is ArpItemViewModel entry
        && (string.IsNullOrWhiteSpace(ArpSearchText) || entry.Matches(ArpSearchText.Trim()));

    private Task RefreshAsync()
    {
        _deviceMonitor.RequestRefresh();
        _sensorMonitor.RequestRefresh();
        _alertMonitor.RequestRefresh();
        return Task.WhenAll(
            LoadAlertHistoryAsync(), LoadPortsAsync(), LoadResourcesAsync(), LoadAvailabilityAsync(),
            LoadDeviceGroupsAsync(), LoadVlansAsync(), LoadFdbAsync(), LoadArpAsync(), LoadEventLogAsync());
    }

    /// <summary>
    /// Asks LibreNMS to rediscover this device now (see <see cref="IDevicesApi.DiscoverAsync"/>).
    /// LibreNMS applies the result on its own schedule - this only confirms
    /// the request was queued, it does not wait for or reload anything here.
    /// </summary>
    private async Task RediscoverAsync()
    {
        IsRediscovering = true;

        try
        {
            var message = await _client.Devices.DiscoverAsync(_deviceId).ConfigureAwait(true);
            _windows.ShowInformation("Rediscover requested", message);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not trigger rediscovery for device {DeviceId}", _deviceId);
            _windows.ShowError("Rediscover failed", ex.ToUserMessage());
        }
        finally
        {
            IsRediscovering = false;
        }
    }

    /// <summary>
    /// Permanently removes this device from LibreNMS (LibreNMS itself does
    /// not support a "soft" delete/undelete for devices, only <see cref="Device.Ignore"/>/
    /// <see cref="Device.Disabled"/>, which are separate operations), after an
    /// explicit confirmation naming the device. On success, closes this
    /// window and refreshes the device list so the removed device disappears
    /// from it immediately rather than on the next timed poll.
    /// </summary>
    private async Task DeleteAsync()
    {
        var name = _device?.BestName ?? _editHostname;
        if (!_windows.Confirm(
                "Delete device",
                $"Permanently delete '{name}' from LibreNMS? This cannot be undone."))
        {
            return;
        }

        IsDeleting = true;

        try
        {
            var message = await _client.Devices.DeleteAsync(_deviceId).ConfigureAwait(true);
            ForgetPinnedAndRecentlyViewed();
            _deviceMonitor.RequestRefresh();
            _windows.ShowInformation("Device deleted", message);
            _windows.CloseDeviceDetail(_deviceId);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not delete device {DeviceId}", _deviceId);
            _windows.ShowError("Delete failed", ex.ToUserMessage());
        }
        finally
        {
            IsDeleting = false;
        }
    }

    /// <summary>
    /// Navigates to the Edit section, resetting its draft fields from the
    /// current device every time - switching away and back discards an
    /// unsaved edit rather than leaving stale text sitting there.
    /// </summary>
    private void SelectEdit()
    {
        EditHostname = _device?.Hostname ?? string.Empty;
        EditLocation = _device?.Location ?? string.Empty;
        EditDisplayName = _device?.Display ?? string.Empty;
        EditType = _device?.Type ?? string.Empty;
        EditPurpose = _device?.Purpose ?? string.Empty;
        EditOverrideSysLocation = _device?.OverrideSysLocation ?? false;
        EditContact = _device?.Contact ?? string.Empty;
        EditOverrideSysContact = _device?.OverrideSysContact ?? false;
        EditNotes = _device?.Notes ?? string.Empty;
        EditDisabled = _device?.Disabled ?? false;
        EditIgnore = _device?.Ignore ?? false;
        EditIgnoreStatus = _device?.IgnoreStatus ?? false;
        SelectedPollerGroup = PollerGroups.FirstOrDefault(g => g.Id == _device?.PollerGroup) ?? DefaultPollerGroup;

        // Never pre-filled from the device - see EditChangeSnmp's remarks.
        EditChangeSnmp = false;
        SetEditPingOnly(false);
        SetEditSnmpVersion(v2c: true);
        EditCommunity = string.Empty;
        EditAuthLevel = "authPriv";
        EditAuthName = string.Empty;
        EditAuthPass = string.Empty;
        EditAuthAlgo = "SHA";
        EditCryptoPass = string.Empty;
        EditCryptoAlgo = "AES";
        EditSnmpOs = "ping";
        EditSysNameOverride = string.Empty;
        EditHardwareOverride = string.Empty;
        EditPort = string.Empty;
        EditTransport = string.Empty;

        EditErrorMessage = null;
        EditSuccessMessage = null;

        if (!_hasLoadedPollerGroupsOnce)
        {
            _hasLoadedPollerGroupsOnce = true;
            _ = LoadPollerGroupsAsync();
            _ = LoadKnownOperatingSystemsAsync();
        }

        SelectedSection = DeviceDetailSection.Edit;
    }

    private async Task LoadPollerGroupsAsync()
    {
        try
        {
            var groups = await _client.PollerGroups.ListAsync(_loadCts.Token).ConfigureAwait(true);

            foreach (var group in groups)
            {
                // Skip a real id-0 row rather than showing two "poller 0"
                // entries side by side - LibreNMS itself treats 0 as the
                // implicit default regardless of whether a row exists for it.
                if (group.Id != 0)
                {
                    PollerGroups.Add(group);
                }
            }

            // The device's own poller group may only now be resolvable to a
            // real entry (rather than the synthetic default) now that the
            // full list has arrived.
            SelectedPollerGroup = PollerGroups.FirstOrDefault(g => g.Id == _device?.PollerGroup) ?? DefaultPollerGroup;
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load poller groups");
        }
    }

    private async Task LoadKnownOperatingSystemsAsync()
    {
        try
        {
            var devices = await _client.Devices.ListAsync(_loadCts.Token).ConfigureAwait(true);

            var distinctOperatingSystems = devices
                .Select(d => d.Os)
                .Where(os => !string.IsNullOrWhiteSpace(os))
                .Select(os => os!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(os => !os.Equals("ping", StringComparison.OrdinalIgnoreCase))
                .OrderBy(os => os, StringComparer.OrdinalIgnoreCase);

            foreach (var os in distinctOperatingSystems)
            {
                KnownOperatingSystems.Add(os);
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed while this was in flight - quietly give up.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load the known OS list from the device fleet");
        }
    }

    /// <summary>
    /// Saves only the fields that actually changed from what LibreNMS
    /// already has - an unchanged field is left out of the request entirely
    /// rather than round-tripping its current value back to itself. A
    /// hostname change is a separate rename call (the polled identifier
    /// itself, not a stored column), sent first so the rest of the batch
    /// still targets the same device id regardless of the new hostname.
    /// </summary>
    private async Task SaveEditAsync()
    {
        var fields = new Dictionary<string, string?>();
        var newHostname = EditHostname.Trim();
        var hostnameChanged = newHostname.Length > 0 && newHostname != (_device?.Hostname ?? string.Empty);

        if (EditLocation.Trim() != (_device?.Location ?? string.Empty))
        {
            fields["location"] = string.IsNullOrWhiteSpace(EditLocation) ? null : EditLocation.Trim();
        }

        if (EditDisplayName.Trim() != (_device?.Display ?? string.Empty))
        {
            fields["display"] = string.IsNullOrWhiteSpace(EditDisplayName) ? null : EditDisplayName.Trim();
        }

        if (EditType.Trim() != (_device?.Type ?? string.Empty))
        {
            fields["type"] = string.IsNullOrWhiteSpace(EditType) ? null : EditType.Trim();
        }

        if (EditPurpose.Trim() != (_device?.Purpose ?? string.Empty))
        {
            fields["purpose"] = string.IsNullOrWhiteSpace(EditPurpose) ? null : EditPurpose.Trim();
        }

        if (EditOverrideSysLocation != (_device?.OverrideSysLocation ?? false))
        {
            fields["override_sysLocation"] = EditOverrideSysLocation ? "1" : "0";
        }

        if (EditContact.Trim() != (_device?.Contact ?? string.Empty))
        {
            fields["sysContact"] = string.IsNullOrWhiteSpace(EditContact) ? null : EditContact.Trim();
        }

        if (EditOverrideSysContact != (_device?.OverrideSysContact ?? false))
        {
            fields["override_sysContact"] = EditOverrideSysContact ? "1" : "0";
        }

        if (EditNotes.Trim() != (_device?.Notes ?? string.Empty))
        {
            fields["notes"] = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes.Trim();
        }

        if (EditDisabled != (_device?.Disabled ?? false))
        {
            fields["disabled"] = EditDisabled ? "1" : "0";
        }

        if (EditIgnore != (_device?.Ignore ?? false))
        {
            fields["ignore"] = EditIgnore ? "1" : "0";
        }

        if (EditIgnoreStatus != (_device?.IgnoreStatus ?? false))
        {
            fields["ignore_status"] = EditIgnoreStatus ? "1" : "0";
        }

        if (SelectedPollerGroup.Id != (_device?.PollerGroup ?? 0))
        {
            fields["poller_group"] = SelectedPollerGroup.Id.ToString(CultureInfo.InvariantCulture);
        }

        // Only included when explicitly opted into (EditChangeSnmp) - see its
        // own remarks for why this can never be an accidental side effect of
        // saving the rest of the form.
        if (EditChangeSnmp)
        {
            if (EditIsPingOnly)
            {
                fields["snmp_disable"] = "1";
                fields["os"] = string.IsNullOrWhiteSpace(EditSnmpOs) ? "ping" : EditSnmpOs.Trim();
                fields["sysName"] = string.IsNullOrWhiteSpace(EditSysNameOverride) ? null : EditSysNameOverride.Trim();
                fields["hardware"] = string.IsNullOrWhiteSpace(EditHardwareOverride) ? null : EditHardwareOverride.Trim();
            }
            else
            {
                fields["snmp_disable"] = "0";
                fields["snmpver"] = EditIsSnmpV3 ? "v3" : EditIsSnmpV1 ? "v1" : "v2c";

                if (EditIsSnmpV3)
                {
                    fields["authlevel"] = EditAuthLevel;
                    fields["authname"] = string.IsNullOrWhiteSpace(EditAuthName) ? null : EditAuthName.Trim();
                    fields["authpass"] = string.IsNullOrWhiteSpace(EditAuthPass) ? null : EditAuthPass;
                    fields["authalgo"] = EditAuthAlgo;
                    fields["cryptopass"] = string.IsNullOrWhiteSpace(EditCryptoPass) ? null : EditCryptoPass;
                    fields["cryptoalgo"] = EditCryptoAlgo;
                }
                else
                {
                    fields["community"] = string.IsNullOrWhiteSpace(EditCommunity) ? null : EditCommunity.Trim();
                }
            }

            if (int.TryParse(EditPort, out var port) && port > 0)
            {
                fields["port"] = port.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(EditTransport))
            {
                fields["transport"] = EditTransport.Trim();
            }
        }

        if (!hostnameChanged && fields.Count == 0)
        {
            EditSuccessMessage = "Nothing to save.";
            EditErrorMessage = null;
            return;
        }

        EditErrorMessage = null;
        EditSuccessMessage = null;
        IsSavingEdit = true;

        // Rename and the field-update batch are independent LibreNMS calls,
        // so one failing must not silently swallow the other - a rename
        // failure used to abort the whole save before the rest of the
        // batch (e.g. Display name) was even attempted, since both used to
        // share one try/catch.
        string? renameError = null;
        string? fieldsError = null;

        if (hostnameChanged)
        {
            try
            {
                await _client.Devices.RenameAsync(_deviceId, newHostname).ConfigureAwait(true);
                if (_device is not null)
                {
                    _device.Hostname = newHostname;
                }
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogWarning(ex, "Could not rename device {DeviceId} to {NewHostname}", _deviceId, newHostname);
                renameError = ex.ToUserMessage();
            }
        }

        if (fields.Count > 0)
        {
            try
            {
                await _client.Devices.UpdateFieldsAsync(_deviceId, fields).ConfigureAwait(true);

                // Applied locally immediately rather than waiting for the
                // next shared poll, so Overview and the header reflect the
                // edit right away instead of looking like it did nothing.
                if (_device is not null)
                {
                    if (fields.TryGetValue("location", out var location)) _device.Location = location;
                    if (fields.TryGetValue("display", out var display)) _device.Display = display;
                    if (fields.TryGetValue("type", out var type)) _device.Type = type;
                    if (fields.TryGetValue("purpose", out var purpose)) _device.Purpose = purpose;
                    if (fields.ContainsKey("override_sysLocation")) _device.OverrideSysLocation = EditOverrideSysLocation;
                    if (fields.TryGetValue("sysContact", out var contact)) _device.Contact = contact;
                    if (fields.ContainsKey("override_sysContact")) _device.OverrideSysContact = EditOverrideSysContact;
                    if (fields.TryGetValue("notes", out var notes)) _device.Notes = notes;
                    if (fields.ContainsKey("disabled")) _device.Disabled = EditDisabled;
                    if (fields.ContainsKey("ignore")) _device.Ignore = EditIgnore;
                    if (fields.ContainsKey("ignore_status")) _device.IgnoreStatus = EditIgnoreStatus;
                    if (fields.ContainsKey("poller_group")) _device.PollerGroup = SelectedPollerGroup.Id;
                }
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogWarning(ex, "Could not update device {DeviceId}", _deviceId);
                fieldsError = ex.ToUserMessage();
            }
        }

        RaiseDeviceChanged();

        if (renameError is null && fieldsError is null)
        {
            EditSuccessMessage = "Saved.";
        }
        else if (renameError is not null && fieldsError is not null)
        {
            EditErrorMessage = $"Rename failed: {renameError} Other fields also failed: {fieldsError}";
        }
        else if (renameError is not null)
        {
            EditErrorMessage = $"Rename failed: {renameError}";
            EditSuccessMessage = fields.Count > 0 ? "The rest of the form saved." : null;
        }
        else
        {
            EditErrorMessage = $"Save failed: {fieldsError}";
            EditSuccessMessage = hostnameChanged ? "The rename saved." : null;
        }

        IsSavingEdit = false;
    }

    private void RaiseDeviceChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AlternateName));
        OnPropertyChanged(nameof(HasAlternateName));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Ip));
        OnPropertyChanged(nameof(Os));
        OnPropertyChanged(nameof(Hardware));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(HasLoaded));
        OnPropertyChanged(nameof(IsLoadingDevice));
        OnPropertyChanged(nameof(SysDescr));
        OnPropertyChanged(nameof(HasSysDescr));
        OnPropertyChanged(nameof(Serial));
        OnPropertyChanged(nameof(HasSerial));
        OnPropertyChanged(nameof(Contact));
        OnPropertyChanged(nameof(HasContact));
        OnPropertyChanged(nameof(SysObjectId));
        OnPropertyChanged(nameof(HasSysObjectId));
        OnPropertyChanged(nameof(DependencyParentText));
        OnPropertyChanged(nameof(HasDependencyParent));
        OnPropertyChanged(nameof(InsertedText));
        OnPropertyChanged(nameof(HasInserted));
        OnPropertyChanged(nameof(LastDiscoveredText));
        OnPropertyChanged(nameof(HasLastDiscovered));
        OnPropertyChanged(nameof(HasTechnicalDetails));
        OnPropertyChanged(nameof(HasLocation));
        ShowDevicesForLocationCommand.RaiseCanExecuteChanged();

        OnPropertyChanged(nameof(CanOpenExternally));
        OpenWebHttpCommand.RaiseCanExecuteChanged();
        OpenWebHttpsCommand.RaiseCanExecuteChanged();
        OpenTelnetCommand.RaiseCanExecuteChanged();
        OpenSshCommand.RaiseCanExecuteChanged();
    }

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value!;

    private static string TitleCase(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : char.ToUpperInvariant(value[0]) + value[1..];

    private static string FormatUptime(long seconds)
    {
        if (seconds <= 0)
        {
            return "-";
        }

        var span = TimeSpan.FromSeconds(seconds);

        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    public void Dispose()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _deviceMonitor.Polled -= OnDevicePolled;
        _sensorMonitor.Polled -= OnSensorPolled;
        _alertMonitor.Polled -= OnAlertsPolled;
    }

    /// <summary>
    /// Classifies a reading purely against the limits LibreNMS itself has
    /// configured on that specific sensor (sensor_limit/_warn/_low/_low_warn) -
    /// the fallback for every sensor class outside <see cref="SensorCategoryRegistry"/>
    /// (voltage, current, power, ...), which have no app-wide setting to fall
    /// back to at all, unlike dBm/signal/temperature/fan speed (see
    /// <see cref="HybridThresholdEvaluator"/>, used for those via the registry).
    /// </summary>
    private sealed class SensorLimitThresholdEvaluator : IThresholdEvaluator
    {
        private readonly Sensor _sensor;

        public SensorLimitThresholdEvaluator(Sensor sensor) => _sensor = sensor;

        public AlertSeverity Evaluate(double value)
        {
            if (_sensor.LimitLow is null && _sensor.LimitLowWarn is null
                && _sensor.LimitHigh is null && _sensor.LimitHighWarn is null)
            {
                // Nothing configured on this sensor to judge it against -
                // "Unknown" reads as neutral, not as if it were fine.
                return AlertSeverity.Unknown;
            }

            if (_sensor.LimitLow is { } low && value <= low)
            {
                return AlertSeverity.Critical;
            }

            if (_sensor.LimitHigh is { } high && value >= high)
            {
                return AlertSeverity.Critical;
            }

            if (_sensor.LimitLowWarn is { } lowWarn && value <= lowWarn)
            {
                return AlertSeverity.Warning;
            }

            if (_sensor.LimitHighWarn is { } highWarn && value >= highWarn)
            {
                return AlertSeverity.Warning;
            }

            return AlertSeverity.Ok;
        }
    }
}

/// <summary>
/// Units for LibreNMS sensor classes outside <see cref="SensorCategoryRegistry"/>
/// (which already carries a unit for its four) - the API does not return a
/// unit string, so only classes worth labelling with confidence are included;
/// anything else (state, count, runtime, ...) is left bare rather than guessed.
/// </summary>
file static class SensorUnitDisplay
{
    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["voltage"] = " V",
        ["current"] = " A",
        ["power"] = " W",
        ["frequency"] = " Hz",
        ["humidity"] = "%",
        ["storage"] = "%",
        ["charge"] = "%",
        ["load"] = "%",
    };

    public static string Resolve(string? sensorClass) =>
        sensorClass is not null && Units.TryGetValue(sensorClass, out var unit) ? unit : string.Empty;
}

/// <summary>Common IANAifType values translated to what LibreNMS's own UI calls them, since the raw MIB enum name is not user-friendly.</summary>
file static class IfTypeDisplay
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ethernetCsmacd"] = "Ethernet",
        ["ieee8023adLag"] = "Link aggregate",
        ["l2vlan"] = "L2 VLAN",
        ["propVirtual"] = "Virtual",
        ["softwareLoopback"] = "Loopback",
        ["tunnel"] = "Tunnel",
        ["other"] = "Other",
    };

    public static string Resolve(string? ifType) =>
        string.IsNullOrWhiteSpace(ifType) ? "-" : Names.GetValueOrDefault(ifType, ifType);
}

/// <summary>One group of related sensor readings on a device's Sensors tab.</summary>
public sealed class SensorGroupViewModel
{
    public SensorGroupViewModel(string name, IReadOnlyList<SensorItemViewModel> sensors)
    {
        Name = name;
        Sensors = sensors;
    }

    public string Name { get; }

    public IReadOnlyList<SensorItemViewModel> Sensors { get; }

    public int Count => Sensors.Count;

    /// <summary>The worst severity in the group, so a header says at a glance whether anything inside needs attention.</summary>
    public AlertSeverity WorstSeverity => Sensors.Count == 0
        ? AlertSeverity.Unknown
        : Sensors.OrderByDescending(s => s.Severity.SortRank()).First().Severity;
}

/// <summary>One row in a device's event log - a general audit entry, not necessarily tied to any alert.</summary>
public sealed class EventLogItemViewModel
{
    private readonly EventLogEntry _entry;

    public EventLogItemViewModel(EventLogEntry entry) => _entry = entry;

    public string Message => string.IsNullOrWhiteSpace(_entry.Message) ? "-" : _entry.Message!;

    public string TypeText => string.IsNullOrWhiteSpace(_entry.Type) ? "-" : _entry.Type!;

    public string Username => string.IsNullOrWhiteSpace(_entry.Username) ? "-" : _entry.Username!;

    public string TimeText => _entry.Timestamp is { } t
        ? t.ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)
        : "-";
}

/// <summary>One row in a device's Ports tab - one network interface.</summary>
public sealed class PortItemViewModel
{
    private readonly Port _port;
    private readonly NetworkLink? _link;
    private readonly IReadOnlyList<DeviceIpAddress> _addresses;
    private readonly IWindowService _windows;

    public PortItemViewModel(Port port, NetworkLink? link, IReadOnlyList<DeviceIpAddress> addresses, IWindowService windows)
    {
        _port = port;
        _link = link;
        _addresses = addresses;
        _windows = windows;

        OpenNeighborCommand = new RelayCommand(
            () => _windows.ShowDeviceDetail(_link!.RemoteDeviceId!.Value),
            () => _link?.RemoteDeviceId is > 0);
    }

    public Port Model => _port;

    /// <summary>
    /// Every address bound to this interface, comma-joined - a secondary
    /// address, or an HSRP/VRRP virtual alongside the real one, means this is
    /// not always exactly one. Empty for the many ports (access switchports,
    /// an unrouted management VLAN) that carry no address at all.
    /// </summary>
    public string IpAddressesText => _addresses.Count == 0
        ? string.Empty
        : string.Join(", ", _addresses.Select(a => a.DisplayText));

    public bool HasIpAddresses => _addresses.Count > 0;

    public string DisplayName => _port.DisplayName;

    /// <summary>The operator's own description, shown as a subtitle under the port's identity when set and distinct from it.</summary>
    public string? SecondaryName => !string.IsNullOrWhiteSpace(_port.IfAlias)
        && !string.Equals(_port.IfAlias, DisplayName, StringComparison.OrdinalIgnoreCase)
        ? _port.IfAlias
        : null;

    public bool HasSecondaryName => SecondaryName is not null;

    public bool IsUp => _port.IsUp;

    public bool HasKnownStatus => !string.IsNullOrWhiteSpace(_port.IfOperStatus);

    /// <summary>Reuses the app's existing severity colours: unknown status reads as neutral, not as if it were down.</summary>
    public AlertSeverity StatusSeverity => !HasKnownStatus ? AlertSeverity.Unknown : (IsUp ? AlertSeverity.Ok : AlertSeverity.Critical);

    public string StatusText => HasKnownStatus ? Capitalise(_port.IfOperStatus!) : "Unknown";

    public string SpeedText => FormatBitsPerSecond(_port.IfSpeed);

    public string InRateText => _port.IfInOctetsRate is { } rate ? FormatBitsPerSecond((long)(rate * 8)) : "-";

    public string OutRateText => _port.IfOutOctetsRate is { } rate ? FormatBitsPerSecond((long)(rate * 8)) : "-";

    public long ErrorCount => (_port.IfInErrorsDelta ?? 0) + (_port.IfOutErrorsDelta ?? 0);

    public bool HasErrors => ErrorCount > 0;

    /// <summary>e.g. "Ethernet", second line "fullDuplex" - matches how LibreNMS's own port page labels this.</summary>
    public string MediaText => IfTypeDisplay.Resolve(_port.IfType);

    public string? DuplexText => string.IsNullOrWhiteSpace(_port.IfDuplex) || string.Equals(_port.IfDuplex, "unknown", StringComparison.OrdinalIgnoreCase)
        ? null
        : _port.IfDuplex;

    public bool HasDuplex => DuplexText is not null;

    /// <summary>Colon-separated, since SNMP hands this back as a bare hex string.</summary>
    public string MacAddressText => MacAddressFormat.Format(_port.IfPhysAddress);

    public string MtuText => _port.IfMtu is { } mtu && mtu > 0 ? mtu.ToString(CultureInfo.InvariantCulture) : "-";

    public RelayCommand OpenNeighborCommand { get; }

    public bool HasNeighbor => _link is not null;

    /// <summary>e.g. "r-sw-pit-10 (Gi0/1)" - the device and port this one is physically connected to, if LibreNMS has discovered one.</summary>
    public string? NeighborText => _link is null
        ? null
        : string.IsNullOrWhiteSpace(_link.RemotePort) ? _link.DisplayRemoteName : $"{_link.DisplayRemoteName} ({_link.RemotePort})";

    /// <summary>True only when the neighbour is itself a device this LibreNMS instance monitors, so there is somewhere to jump to.</summary>
    public bool CanOpenNeighbor => _link?.RemoteDeviceId is > 0;

    public bool Matches(string term) =>
        DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (_port.IfName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (_port.IfAlias?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string Capitalise(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string FormatBitsPerSecond(long? bitsPerSecond)
    {
        if (bitsPerSecond is not { } bps || bps <= 0)
        {
            return "-";
        }

        string[] units = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        double value = bps;
        var unit = 0;

        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

/// <summary>
/// One row in a device's FDB tab - one MAC address the switch has learned.
/// An <see cref="ObservableObject"/> (not a plain class) specifically so
/// <see cref="RefreshPortName"/> can be called once port names resolve after
/// this row was already created and shown - see its remarks.
/// </summary>
public sealed class FdbItemViewModel : ObservableObject
{
    private readonly FdbEntry _entry;
    private readonly Dictionary<int, string> _portNamesByPortId;
    private readonly Dictionary<int, Vlan> _vlansById;

    public FdbItemViewModel(FdbEntry entry, Dictionary<int, string> portNamesByPortId, Dictionary<int, Vlan> vlansById)
    {
        _entry = entry;
        _portNamesByPortId = portNamesByPortId;
        _vlansById = vlansById;
    }

    public string MacAddressText => MacAddressFormat.Format(_entry.MacAddress);

    /// <summary>
    /// The port's display name if Ports has resolved it yet, otherwise a
    /// bare id as a fallback - see <see cref="DeviceDetailViewModel._portNamesByPortId"/>'s
    /// remarks. Unlike when this fallback was first written, Ports loading
    /// after FDB (its own neighbour/IP-address lookups make it the slower of
    /// the two more often than not) is not rare in practice, so
    /// <see cref="RefreshPortName"/> exists to correct this once that
    /// happens instead of leaving the fallback showing for good.
    /// </summary>
    public string PortText => _portNamesByPortId.TryGetValue(_entry.PortId, out var name)
        ? name
        : string.Create(CultureInfo.InvariantCulture, $"Port {_entry.PortId}");

    /// <summary>
    /// The real 802.1Q VLAN number, resolved via <see cref="_vlansById"/> -
    /// <see cref="FdbEntry.VlanId"/> is LibreNMS's own internal id for the
    /// VLAN row, not the tag itself (confirmed against a live server: a
    /// vlan_id of 50 on a device whose actual VLANs were all four digits).
    /// Falls back to the raw id if it cannot be resolved (VLANs failed to
    /// load, or a stale/unknown id), which is no worse than showing the
    /// wrong number outright, just not corrected.
    /// </summary>
    public string VlanText
    {
        get
        {
            if (_entry.VlanId is not { } vlanId || vlanId <= 0)
            {
                return "-";
            }

            return _vlansById.TryGetValue(vlanId, out var vlan)
                ? vlan.VlanNumber.ToString(CultureInfo.InvariantCulture)
                : vlanId.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The VLAN's own name, e.g. "I-MGMT-Intern" - shown as a subtitle under the number when known.</summary>
    public string? VlanNameText => _entry.VlanId is { } vlanId
        && _vlansById.TryGetValue(vlanId, out var vlan)
        && !string.IsNullOrWhiteSpace(vlan.VlanName)
            ? vlan.VlanName
            : null;

    public bool HasVlanName => VlanNameText is not null;

    public string UpdatedText => _entry.UpdatedAt is { } t
        ? t.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)
        : "-";

    public bool Matches(string term) =>
        MacAddressText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || PortText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || VlanText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (VlanNameText?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Called once Ports finishes loading, in case it resolved after this row was already created - see <see cref="PortText"/>'s remarks.</summary>
    public void RefreshPortName() => OnPropertyChanged(nameof(PortText));

    /// <summary>Called once VLANs finishes loading, in case it resolved after this row was already created - see <see cref="VlanText"/>'s remarks.</summary>
    public void RefreshVlan()
    {
        OnPropertyChanged(nameof(VlanText));
        OnPropertyChanged(nameof(VlanNameText));
        OnPropertyChanged(nameof(HasVlanName));
    }
}

/// <summary>One row in a device's VLANs tab - one VLAN configured on it.</summary>
public sealed class VlanItemViewModel : ObservableObject
{
    private readonly Vlan _vlan;
    private readonly ObservableCollection<PortItemViewModel> _allPorts;

    public VlanItemViewModel(Vlan vlan, ObservableCollection<PortItemViewModel> allPorts)
    {
        _vlan = vlan;
        _allPorts = allPorts;
    }

    public string NumberText => _vlan.VlanNumber.ToString(CultureInfo.InvariantCulture);

    public string NameText => string.IsNullOrWhiteSpace(_vlan.VlanName) ? "-" : _vlan.VlanName!;

    /// <summary>
    /// Every port whose own untagged/native VLAN (Port.IfVlan) matches this
    /// one - NOT full trunk membership. LibreNMS's per-VLAN trunk-membership
    /// endpoint (/devices/{id}/ports/vlan/{vlan}) would cover a trunk port
    /// carrying this VLAN tagged too, but it 500s unconditionally on every
    /// server tried this was built against (confirmed with several id/format
    /// variations, and with no working alternative route found) - a bug in
    /// that LibreNMS build, not something fixable from here. This is the
    /// reliable subset the API actually gives back. Queried live against the
    /// shared Ports collection rather than cached, so a Ports refresh is
    /// reflected without this row needing to rebuild.
    /// </summary>
    public IReadOnlyList<PortItemViewModel> AccessPorts =>
        _allPorts.Where(p => p.Model.IfVlan == _vlan.VlanNumber).ToList();

    /// <summary>
    /// "12 ports: Gi1/1, Gi1/2, ..." or "-" for none - one string doing
    /// double duty as the DataGrid cell (ellipsis-trimmed) and its tooltip
    /// (shown in full), rather than a separate count column plus a
    /// truncated-with-"+N more" string to keep in sync with it. Deliberately
    /// labelled "access ports" (see the column header in DeviceView.xaml),
    /// not just "ports", so it doesn't imply full trunk membership - see
    /// AccessPorts' remarks.
    /// </summary>
    public string AccessPortsSummaryText
    {
        get
        {
            var ports = AccessPorts;
            if (ports.Count == 0)
            {
                return "-";
            }

            var countLabel = ports.Count == 1 ? "1 port: " : $"{ports.Count} ports: ";
            return countLabel + string.Join(", ", ports.Select(p => p.DisplayName));
        }
    }

    public bool Matches(string term) =>
        NumberText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || NameText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || AccessPorts.Any(p => p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>Called once Ports finishes loading (or reloads), in case it resolved after or changed since this row was already created - see <see cref="AccessPorts"/>'s remarks.</summary>
    public void RefreshPorts() => OnPropertyChanged(nameof(AccessPortsSummaryText));
}

/// <summary>
/// One row in a device's ARP tab - one IPv4-to-MAC mapping the device has
/// resolved. An <see cref="ObservableObject"/> for the same reason as
/// <see cref="FdbItemViewModel"/> - see its <see cref="FdbItemViewModel.RefreshPortName"/> remarks.
/// </summary>
public sealed class ArpItemViewModel : ObservableObject
{
    private readonly ArpEntry _entry;
    private readonly Dictionary<int, string> _portNamesByPortId;

    public ArpItemViewModel(ArpEntry entry, Dictionary<int, string> portNamesByPortId)
    {
        _entry = entry;
        _portNamesByPortId = portNamesByPortId;
    }

    public string Ipv4AddressText => string.IsNullOrWhiteSpace(_entry.Ipv4Address) ? "-" : _entry.Ipv4Address!;

    public string MacAddressText => MacAddressFormat.Format(_entry.MacAddress);

    /// <summary>See <see cref="FdbItemViewModel.PortText"/> - same lookup, same fallback, same reason it can need <see cref="RefreshPortName"/>.</summary>
    public string PortText => _portNamesByPortId.TryGetValue(_entry.PortId, out var name)
        ? name
        : string.Create(CultureInfo.InvariantCulture, $"Port {_entry.PortId}");

    public bool Matches(string term) =>
        Ipv4AddressText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || MacAddressText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || PortText.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>Called once Ports finishes loading, in case it resolved after this row was already created - see <see cref="PortText"/>'s remarks.</summary>
    public void RefreshPortName() => OnPropertyChanged(nameof(PortText));
}

/// <summary>One row in a device's currently active alerts, shown on the Overview tab.</summary>
public sealed class ActiveAlertItemViewModel
{
    private readonly Alert _alert;
    private readonly bool _serverTimestampsAreUtc;

    public ActiveAlertItemViewModel(Alert alert, bool serverTimestampsAreUtc)
    {
        _alert = alert;
        _serverTimestampsAreUtc = serverTimestampsAreUtc;
    }

    public string RuleName => _alert.DisplayRuleName;

    public AlertSeverity Severity => _alert.Severity;

    public string SeverityText => Severity.ToDisplayString();

    public AlertState State => _alert.State;

    public string StateText => State.ToDisplayString();

    private DateTime? LocalTimestamp => _alert.Timestamp is { } t
        ? (_serverTimestampsAreUtc ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime() : t)
        : null;

    public string AgeText => LocalTimestamp is { } local
        ? FormatAge(DateTime.Now - local)
        : "-";

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalDays >= 1)
        {
            return $"{(int)age.TotalDays}d {age.Hours}h";
        }

        if (age.TotalHours >= 1)
        {
            return $"{(int)age.TotalHours}h {age.Minutes}m";
        }

        return $"{Math.Max(1, (int)age.TotalMinutes)}m";
    }
}

/// <summary>One row in a device's alert history (<c>/api/v0/logs/alertlog</c>).</summary>
public sealed class AlertLogItemViewModel
{
    private readonly AlertLogEntry _entry;
    private readonly AlertRule? _rule;
    private readonly AlertDetail _detail;

    public AlertLogItemViewModel(AlertLogEntry entry, AlertRule? rule, AlertDetail detail)
    {
        _entry = entry;
        _rule = rule;
        _detail = detail;
    }

    public string TimeText => _entry.TimeLogged is { } t
        ? t.ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)
        : "-";

    public string RuleName => _rule?.Name ?? $"Rule {_entry.RuleId}";

    public AlertSeverity Severity => _rule?.Severity ?? AlertSeverity.Unknown;

    public string SeverityText => Severity == AlertSeverity.Unknown ? "-" : Severity.ToDisplayString();

    public AlertState State => _entry.State;

    public string StateText => State.ToDisplayString();

    /// <summary>
    /// What actually matched, e.g. "Gi0/0/1 - uplink to core - ifOperStatus =
    /// down", so the row says what happened without cross-referencing the
    /// Alerts tab. Empty when LibreNMS recorded no detail for this entry
    /// (older rows, or a rule with no matched-row data).
    /// </summary>
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
            var suffix = fields.Count > 0 ? " - " + string.Join(", ", fields.Select(f => $"{f.Name} = {f.Value}")) : string.Empty;
            var more = _detail.Faults.Count > 1 ? $" (+{_detail.Faults.Count - 1} more)" : string.Empty;

            return fault.Title + suffix + more;
        }
    }

    public bool HasDetail => DetailText.Length > 0;
}

/// <summary>One row in a device's Resources tab - one CPU/core.</summary>
public sealed class ProcessorItemViewModel
{
    private readonly ProcessorSensor _processor;

    public ProcessorItemViewModel(ProcessorSensor processor) => _processor = processor;

    public string Description => string.IsNullOrWhiteSpace(_processor.Description) ? "-" : _processor.Description!;

    public double UsagePercent => _processor.UsagePercent ?? 0;

    public string UsageText => _processor.UsagePercent is { } percent ? $"{percent:0.#}%" : "-";

    public AlertSeverity Severity => ResourceSeverity.Evaluate(_processor.UsagePercent, _processor.WarningPercent);
}

/// <summary>One row in a device's Resources tab - one memory pool.</summary>
public sealed class MempoolItemViewModel
{
    private readonly MempoolSensor _mempool;

    public MempoolItemViewModel(MempoolSensor mempool) => _mempool = mempool;

    public string Description => string.IsNullOrWhiteSpace(_mempool.Description) ? "-" : _mempool.Description!;

    public double UsagePercent => _mempool.UsagePercent ?? 0;

    public string UsageText => _mempool.UsagePercent is { } percent ? $"{percent:0.#}%" : "-";

    public string DetailText => ResourceByteFormat.FormatUsedOfTotal(_mempool.UsedBytes, _mempool.TotalBytes);

    public AlertSeverity Severity => ResourceSeverity.Evaluate(_mempool.UsagePercent, _mempool.WarningPercent);
}

/// <summary>One row in a device's Resources tab - one disk/filesystem.</summary>
public sealed class StorageItemViewModel
{
    private readonly StorageVolume _volume;

    public StorageItemViewModel(StorageVolume volume) => _volume = volume;

    public string Description => string.IsNullOrWhiteSpace(_volume.Description) ? "-" : _volume.Description!;

    public double UsagePercent => _volume.UsagePercent ?? 0;

    public string UsageText => _volume.UsagePercent is { } percent ? $"{percent:0.#}%" : "-";

    public string DetailText => ResourceByteFormat.FormatUsedOfTotal(_volume.UsedBytes, _volume.TotalBytes);

    public AlertSeverity Severity => ResourceSeverity.Evaluate(_volume.UsagePercent, _volume.WarningPercent);
}

/// <summary>
/// One row in the Overview's Device Groups card - one LibreNMS device group
/// this device belongs to. Owns its own command (rather than the DeviceView
/// reaching up to a shared one on DeviceDetailViewModel from inside an
/// ItemsControl's DataTemplate) - same pattern as PortItemViewModel's
/// OpenNeighborCommand.
/// </summary>
public sealed class DeviceGroupItemViewModel
{
    public DeviceGroupItemViewModel(DeviceGroup group, Action<string> onSelect)
    {
        Name = group.Name;
        Description = string.IsNullOrWhiteSpace(group.Description) ? null : group.Description;
        ShowDevicesForGroupCommand = new RelayCommand(() => onSelect(Name));
    }

    public string Name { get; }

    /// <summary>The group's own free-text description, e.g. "Wuppertal Devices" for a group named "1044_Wuppertal" - shown as a tooltip, not every group has one.</summary>
    public string? Description { get; }

    /// <summary>Closes this window and jumps to the Devices tab isolated down to this one group - see DeviceDetailViewModel.ShowDevicesForGroup.</summary>
    public RelayCommand ShowDevicesForGroupCommand { get; }
}

/// <summary>One row in a device's Overview "outages" list - a period the device was recorded down.</summary>
public sealed class OutageItemViewModel
{
    private readonly DeviceOutage _outage;

    public OutageItemViewModel(DeviceOutage outage) => _outage = outage;

    public string StartedText => _outage.GoingDown is { } t
        ? t.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
        : "-";

    public string EndedText => _outage.UpAgain is { } t
        ? t.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
        : "ongoing";

    public string DurationText => _outage is { GoingDown: { } start, UpAgain: { } end }
        ? DurationFormat.Format(end - start)
        : "-";
}

/// <summary>
/// One day in the Overview "availability timeline" bar - a compact, at-a-glance
/// history strip (like a status page's uptime bar) rather than the individual
/// incident list <see cref="OutageItemViewModel"/> covers. Deliberately binary
/// (a day either had downtime or it did not) rather than graded by how much,
/// since LibreNMS itself does not track a meaningful "partial" threshold here -
/// the exact amount is still available in <see cref="TooltipText"/>.
/// </summary>
public sealed class OutageDayViewModel
{
    public OutageDayViewModel(DateOnly date, double downSeconds)
    {
        Date = date;
        DownSeconds = downSeconds;
    }

    public DateOnly Date { get; }

    public double DownSeconds { get; }

    public bool HadOutage => DownSeconds > 0;

    public AlertSeverity Severity => HadOutage ? AlertSeverity.Critical : AlertSeverity.Ok;

    public string TooltipText => HadOutage
        ? $"{Date:dd MMM}: down {DurationFormat.Format(TimeSpan.FromSeconds(DownSeconds))}"
        : $"{Date:dd MMM}: no downtime";
}

/// <summary>Colon-separated lowercase hex, since SNMP/LibreNMS hand this back as a bare 12-digit hex string.</summary>
file static class MacAddressFormat
{
    public static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "-";
        }

        var hex = raw.Replace(":", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty);
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
        {
            return raw;
        }

        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToLowerInvariant();
    }
}

file static class DurationFormat
{
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }
}

/// <summary>
/// Shared severity rule for CPU/memory/disk rows. Unlike <see cref="Sensor"/>,
/// LibreNMS only tracks one boundary for these (a single "warning" percent,
/// no separate critical) - so a configured boundary reads as Warning, and a
/// device left unconfigured falls back to fixed, generic bands. Either way,
/// a reading at or past 95% is always Critical: however it is configured,
/// that is no longer a warning.
/// </summary>
file static class ResourceSeverity
{
    private const double AlwaysCriticalAt = 95;
    private const double DefaultWarningAt = 90;

    public static AlertSeverity Evaluate(double? usagePercent, double? warningPercent)
    {
        if (usagePercent is not { } percent)
        {
            return AlertSeverity.Unknown;
        }

        if (percent >= AlwaysCriticalAt)
        {
            return AlertSeverity.Critical;
        }

        var warning = warningPercent ?? DefaultWarningAt;
        return percent >= warning ? AlertSeverity.Warning : AlertSeverity.Ok;
    }
}

/// <summary>Human-readable "12.3 GB / 64 GB" for a memory pool or disk volume.</summary>
file static class ResourceByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string FormatUsedOfTotal(long? usedBytes, long? totalBytes)
    {
        if (usedBytes is not { } used || totalBytes is not { } total || total <= 0)
        {
            return "-";
        }

        return $"{Format(used)} / {Format(total)}";
    }

    private static string Format(long bytes)
    {
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}

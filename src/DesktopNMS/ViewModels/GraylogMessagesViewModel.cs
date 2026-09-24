using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>A syslog level choice - "(4) Warning" - for the Graylog level filter and its default in Settings. A null level is "Any level".</summary>
public sealed record GraylogLevelOption(int? Level, string Label)
{
    /// <summary>Levels 0-7 - what Settings offers as the device tab's default.</summary>
    public static IReadOnlyList<GraylogLevelOption> All { get; } =
        Enumerable.Range(0, 8).Select(l => new GraylogLevelOption(l, GraylogQuery.LevelText(l))).ToList();

    public static GraylogLevelOption Any { get; } = new(null, "Any level");

    /// <summary>"Any level" then 0-7 - the Logs tab's filter, since LibreNMS's fleet-wide page has no level filter until one is picked.</summary>
    public static IReadOnlyList<GraylogLevelOption> AnyAndAll { get; } = new[] { Any }.Concat(All).ToList();

    /// <summary>The option for a level, or "(7) Debug" (every level) for anything out of range.</summary>
    public static GraylogLevelOption For(int level) => All.FirstOrDefault(o => o.Level == level) ?? All[^1];

    public override string ToString() => Label;
}

/// <summary>A stream filter choice; a null <see cref="Id"/> is "All streams".</summary>
public sealed record GraylogStreamOption(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A time range filter choice, in seconds (0 = all time).</summary>
public sealed record GraylogRangeOption(int Seconds, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A device filter choice on the Logs tab: one device, every device at a
/// location (<see cref="LocationDevices"/>), or neither - "All devices".
/// </summary>
public sealed record GraylogDeviceOption(Device? Device, string Label, string? LocationName = null, IReadOnlyList<Device>? LocationDevices = null)
{
    public int? DeviceId => Device?.DeviceId;

    public bool IsLocation => LocationDevices is not null;

    /// <summary>The devices this choice searches - none for "All devices" (no device filter at all).</summary>
    public IReadOnlyList<Device> Devices => LocationDevices ?? (Device is null ? Array.Empty<Device>() : new[] { Device });

    /// <summary>Identifies the choice across refreshes of the device list, so the selection survives one.</summary>
    public string Key => IsLocation ? "location:" + LocationName : Device is null ? "all" : "device:" + Device.DeviceId;

    public override string ToString() => Label;
}

/// <summary>An auto-update interval choice on the Logs tab.</summary>
public sealed record GraylogIntervalOption(int Seconds, string Label)
{
    public static IReadOnlyList<GraylogIntervalOption> All { get; } = new[]
    {
        new GraylogIntervalOption(10, "Every 10 seconds"),
        new GraylogIntervalOption(30, "Every 30 seconds"),
        new GraylogIntervalOption(60, "Every minute"),
        new GraylogIntervalOption(300, "Every 5 minutes"),
    };

    public override string ToString() => Label;
}

/// <summary>
/// Graylog messages, found and filtered the way LibreNMS's own Graylog pages
/// find them (see <see cref="GraylogQuery"/>) - by stream, level, time range
/// and message text, a page at a time, newest first (issue #114). Backs two
/// places:
/// <list type="bullet">
/// <item>Device Details' Integrations, Graylog tab (<see cref="ForDevice"/>) - fixed to one device, like LibreNMS's device Graylog tab.</item>
/// <item>The Logs tab (<see cref="ForFleet"/>) - every device, with a device filter and auto-update, like LibreNMS's Overview, Graylog page.</item>
/// </list>
/// Nothing is fetched until the view is first shown (see <see cref="EnsureLoaded"/>),
/// and only when Graylog is set up at all.
/// </summary>
public sealed class GraylogMessagesViewModel : ObservableObject, IDisposable
{
    private const string NewestFirst = "timestamp:desc";

    /// <summary>Longest query sent - Graylog's search is a GET, and servers and proxies commonly cap a request line at 8 KB; the query roughly doubles once URL-encoded.</summary>
    private const int MaxQueryLength = 3500;

    /// <summary>How long to wait for the hostname lookup LibreNMS does (gethostbyname) before carrying on without it.</summary>
    private static readonly TimeSpan HostnameLookupTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<Device?>? _fixedDevice;
    private readonly IGraylogApi _graylog;
    private readonly ILibreNmsClient _client;
    private readonly IDeviceCache _deviceCache;
    private readonly ISettingsStore _settings;
    private readonly IWindowService? _windows;
    private readonly ILogger _logger;
    private readonly CancellationToken _ownerToken;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer? _autoUpdateTimer;
    private readonly Dictionary<int, IReadOnlyList<string>> _addressesByDevice = new();
    private readonly Dictionary<string, (string Name, int? DeviceId)> _sourceNames = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _searchCts;
    private bool _hasLoaded;
    private bool _hasStarted;
    private bool _suppressReload;
    private bool _isActive;
    private DateTimeOffset? _lastUpdated;
    private string _matchingKey;

    private GraylogStreamOption _selectedStream;
    private GraylogLevelOption _selectedLevel;
    private GraylogRangeOption _selectedRange;
    private GraylogDeviceOption _selectedDevice;
    private int _selectedPageSize;
    private string? _searchText;
    private int _page = 1;
    private long _totalResults;
    private bool _isLoading;
    private bool _isUpdating;
    private string? _errorMessage;
    private string? _updateErrorMessage;
    private GraylogMessageItemViewModel? _selectedMessage;

    private GraylogMessagesViewModel(
        int? fixedDeviceId,
        Func<Device?>? fixedDevice,
        IGraylogApi graylog,
        ILibreNmsClient client,
        IDeviceCache deviceCache,
        ISettingsStore settings,
        IWindowService? windows,
        ILogger logger,
        CancellationToken ownerToken)
    {
        FixedDeviceId = fixedDeviceId;
        _fixedDevice = fixedDevice;
        _graylog = graylog;
        _client = client;
        _deviceCache = deviceCache;
        _settings = settings;
        _windows = windows;
        _logger = logger;
        _ownerToken = ownerToken;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var options = settings.Current.Graylog;

        Streams = new ObservableCollection<GraylogStreamOption> { new(null, "All streams") };
        _selectedStream = Streams[0];

        DeviceOptions = new ObservableCollection<GraylogDeviceOption> { new(null, "All devices") };
        _selectedDevice = DeviceOptions[0];

        RangeOptions = GraylogQuery.Ranges.Select(r => new GraylogRangeOption(r.Seconds, r.Label)).ToList();
        _selectedRange = RangeOptions[0];

        int rows;
        if (IsFleet)
        {
            // LibreNMS's fleet page has no level filter until one is picked.
            LevelOptions = GraylogLevelOption.AnyAndAll;
            _selectedLevel = GraylogLevelOption.Any;
            rows = GraylogSettings.DefaultLogsRowCount;
        }
        else
        {
            // The device tab starts from LibreNMS's device-page settings.
            LevelOptions = GraylogLevelOption.All;
            _selectedLevel = GraylogLevelOption.For(options.DeviceLogLevel);
            rows = Math.Clamp(options.DeviceRowCount, 1, GraylogSettings.MaxRowCount);
        }

        // The starting row count, then LibreNMS's own fixed page sizes.
        PageSizeOptions = new[] { rows, 25, 50, 100, 250 }.Distinct().OrderBy(n => n).ToList();
        _selectedPageSize = rows;

        Messages = new ObservableCollection<GraylogMessageItemViewModel>();
        SelectedMessageFields = Array.Empty<GraylogFieldViewModel>();

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(silent: false), () => !IsLoading);
        PreviousPageCommand = new AsyncRelayCommand(() => GoToPageAsync(Page - 1), () => !IsLoading && Page > 1);
        NextPageCommand = new AsyncRelayCommand(() => GoToPageAsync(Page + 1), () => !IsLoading && Page < PageCount);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        FilterToDeviceCommand = new RelayCommand(p => FilterToDevice(p as GraylogMessageItemViewModel), p => IsFleet && (p as GraylogMessageItemViewModel)?.DeviceId is not null);
        OpenDeviceCommand = new RelayCommand(p => OpenDevice(p as GraylogMessageItemViewModel), p => _windows is not null && (p as GraylogMessageItemViewModel)?.DeviceId is not null);

        if (IsFleet)
        {
            _autoUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SelectedAutoUpdateInterval.Seconds) };
            _autoUpdateTimer.Tick += OnAutoUpdateTick;
        }

        _matchingKey = MatchingKey(options);
        _settings.Changed += OnSettingsChanged;
        _graylog.ConfigurationChanged += OnConfigurationChanged;
    }

    /// <summary>For Device Details' Graylog tab - this one device's messages.</summary>
    public static GraylogMessagesViewModel ForDevice(
        int deviceId,
        Func<Device?> device,
        IGraylogApi graylog,
        ILibreNmsClient client,
        IDeviceCache deviceCache,
        ISettingsStore settings,
        IWindowService windows,
        ILogger logger,
        CancellationToken windowToken) =>
        new(deviceId, device, graylog, client, deviceCache, settings, windows, logger, windowToken);

    /// <summary>For the Logs tab - every device's messages, with a device filter and auto-update.</summary>
    public static GraylogMessagesViewModel ForFleet(
        IGraylogApi graylog,
        ILibreNmsClient client,
        IDeviceCache deviceCache,
        ISettingsStore settings,
        IWindowService windows,
        ILogger logger) =>
        new(null, null, graylog, client, deviceCache, settings, windows, logger, CancellationToken.None);

    /// <summary>The device this view is fixed to, or null on the Logs tab.</summary>
    public int? FixedDeviceId { get; }

    /// <summary>True on the Logs tab - shows the device filter, auto-update and row actions.</summary>
    public bool IsFleet => FixedDeviceId is null;

    /// <summary>Live, so turning Graylog on or off in Settings is picked up (see <see cref="OnSettingsChanged"/>).</summary>
    public bool IsConfigured => _graylog.IsConfigured;

    public ObservableCollection<GraylogStreamOption> Streams { get; }

    public ObservableCollection<GraylogDeviceOption> DeviceOptions { get; }

    public IReadOnlyList<GraylogLevelOption> LevelOptions { get; }

    public IReadOnlyList<GraylogRangeOption> RangeOptions { get; }

    public IReadOnlyList<int> PageSizeOptions { get; }

    public IReadOnlyList<GraylogIntervalOption> AutoUpdateIntervalOptions => GraylogIntervalOption.All;

    public ObservableCollection<GraylogMessageItemViewModel> Messages { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PreviousPageCommand { get; }

    public AsyncRelayCommand NextPageCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>Row action on the Logs tab: filter to the device the message came from.</summary>
    public RelayCommand FilterToDeviceCommand { get; }

    /// <summary>Row action: open the device the message came from in Device Details.</summary>
    public RelayCommand OpenDeviceCommand { get; }

    public GraylogStreamOption SelectedStream
    {
        get => _selectedStream;
        set
        {
            if (value is not null && SetProperty(ref _selectedStream, value))
            {
                ReloadFromFirstPage();
            }
        }
    }

    public GraylogDeviceOption SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is not null && SetProperty(ref _selectedDevice, value))
            {
                ReloadFromFirstPage();
            }
        }
    }

    public GraylogLevelOption SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (value is not null && SetProperty(ref _selectedLevel, value))
            {
                ReloadFromFirstPage();
            }
        }
    }

    public GraylogRangeOption SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is not null && SetProperty(ref _selectedRange, value))
            {
                ReloadFromFirstPage();
            }
        }
    }

    public int SelectedPageSize
    {
        get => _selectedPageSize;
        set
        {
            if (value > 0 && SetProperty(ref _selectedPageSize, value))
            {
                ReloadFromFirstPage();
            }
        }
    }

    /// <summary>Searched for in the message text (LibreNMS's <c>message:"..."</c>) - bound with a short delay, so each keystroke doesn't search.</summary>
    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ReloadFromFirstPage();
            }
        }
    }

    /// <summary>The Logs tab's Auto-update toggle - remembered in settings.</summary>
    public bool AutoUpdate
    {
        get => _settings.Current.Graylog.LogsAutoUpdate;
        set
        {
            if (_settings.Current.Graylog.LogsAutoUpdate == value)
            {
                return;
            }

            _settings.Current.Graylog.LogsAutoUpdate = value;
            _settings.Save();
            OnPropertyChanged();
            UpdateAutoUpdateTimer();

            // Turning it back on catches up straight away rather than after
            // a full interval.
            if (value && _isActive && _hasLoaded && Page == 1)
            {
                _ = LoadAsync(silent: true);
            }
        }
    }

    public GraylogIntervalOption SelectedAutoUpdateInterval
    {
        get => GraylogIntervalOption.All.FirstOrDefault(o => o.Seconds == _settings.Current.Graylog.LogsAutoUpdateSeconds)
               ?? GraylogIntervalOption.All.First(o => o.Seconds == GraylogSettings.DefaultLogsAutoUpdateSeconds);
        set
        {
            if (value is null || _settings.Current.Graylog.LogsAutoUpdateSeconds == value.Seconds)
            {
                return;
            }

            _settings.Current.Graylog.LogsAutoUpdateSeconds = value.Seconds;
            _settings.Save();
            OnPropertyChanged();

            if (_autoUpdateTimer is not null)
            {
                _autoUpdateTimer.Interval = TimeSpan.FromSeconds(value.Seconds);
            }
        }
    }

    /// <summary>"Updated 14:32:05", or why the last automatic update failed (the messages already shown are kept).</summary>
    public string LastUpdatedText
    {
        get
        {
            if (_updateErrorMessage is not null)
            {
                return "Couldn't update: " + _updateErrorMessage;
            }

            if (_lastUpdated is not { } updated)
            {
                return string.Empty;
            }

            var text = "Updated " + updated.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
            return IsFleet && AutoUpdate && Page > 1 ? text + " - auto-update resumes on page 1" : text;
        }
    }

    public bool HasUpdateError => _updateErrorMessage is not null;

    public int Page
    {
        get => _page;
        private set
        {
            if (SetProperty(ref _page, value))
            {
                RaisePagingChanged();
            }
        }
    }

    public long TotalResults
    {
        get => _totalResults;
        private set
        {
            if (SetProperty(ref _totalResults, value))
            {
                RaisePagingChanged();
            }
        }
    }

    public int PageCount => TotalResults <= 0 ? 1 : (int)Math.Min(int.MaxValue, (TotalResults + SelectedPageSize - 1) / SelectedPageSize);

    /// <summary>"1-50 of 1,234 messages", or nothing before the first load.</summary>
    public string PageSummaryText
    {
        get
        {
            if (!_hasLoaded || (IsLoading && Messages.Count == 0))
            {
                return string.Empty;
            }

            if (TotalResults == 0)
            {
                return "No messages";
            }

            var first = ((long)(Page - 1) * SelectedPageSize) + 1;
            var last = first + Messages.Count - 1;
            var noun = TotalResults == 1 ? "message" : "messages";
            return string.Create(CultureInfo.CurrentCulture, $"{first:N0}-{last:N0} of {TotalResults:N0} {noun}");
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                RaisePagingChanged();
                OnPropertyChanged(nameof(ShowEmptyMessage));
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
                OnPropertyChanged(nameof(ShowEmptyMessage));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public bool ShowEmptyMessage => _hasLoaded && !IsLoading && !HasError && Messages.Count == 0;

    public string EmptyMessageText => IsFleet
        ? "No Graylog messages match these filters."
        : "No Graylog messages for this device match these filters.";

    public GraylogMessageItemViewModel? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (SetProperty(ref _selectedMessage, value))
            {
                SelectedMessageFields = value?.Fields ?? Array.Empty<GraylogFieldViewModel>();
                OnPropertyChanged(nameof(SelectedMessageFields));
                OnPropertyChanged(nameof(HasSelectedMessage));
            }
        }
    }

    public bool HasSelectedMessage => _selectedMessage is not null;

    /// <summary>Every field of the selected message, for the details pane.</summary>
    public IReadOnlyList<GraylogFieldViewModel> SelectedMessageFields { get; private set; }

    /// <summary>First load, when the view is first shown - later visits keep what's there (Refresh fetches again).</summary>
    public void EnsureLoaded()
    {
        if (_hasLoaded || IsLoading || !_graylog.IsConfigured)
        {
            return;
        }

        _ = LoadStreamsAsync();
        _ = LoadAsync(silent: false);
    }

    /// <summary>The Logs tab has been shown: load if needed, catch up if an auto-update was missed while away, and start auto-updating.</summary>
    public void Activate()
    {
        _isActive = true;

        if (!_hasLoaded)
        {
            EnsureLoaded();
        }
        else if (IsFleet && AutoUpdate && Page == 1 && _graylog.IsConfigured
                 && (_lastUpdated is null || DateTimeOffset.Now - _lastUpdated >= TimeSpan.FromSeconds(SelectedAutoUpdateInterval.Seconds)))
        {
            _ = LoadAsync(silent: true);
        }

        UpdateAutoUpdateTimer();
    }

    /// <summary>The Logs tab has been left (or the window hidden) - stops auto-updating until it's shown again.</summary>
    public void Deactivate()
    {
        _isActive = false;
        UpdateAutoUpdateTimer();
    }

    /// <summary>Device Details' own Refresh - only re-fetches once the tab has actually been opened.</summary>
    public Task RefreshIfLoadedAsync() => _hasLoaded && _graylog.IsConfigured ? LoadAsync(silent: false) : Task.CompletedTask;

    /// <summary>
    /// Replaces the Logs tab's device filter choices with the current device
    /// list (from the shared device poll): every location that has devices,
    /// then every device - keeping the selected choice when it's still there.
    /// </summary>
    public void UpdateDevices(IReadOnlyList<Device> devices)
    {
        if (!IsFleet)
        {
            return;
        }

        var style = _settings.Current.DeviceNameStyle;

        var locations = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.Location))
            .GroupBy(d => d.Location!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var members = g.OrderBy(d => d.DeviceId).ToList();
                var noun = members.Count == 1 ? "device" : "devices";
                return new GraylogDeviceOption(null, $"{g.Key} (location, {members.Count} {noun})", g.Key, members);
            })
            .OrderBy(o => o.LocationName, StringComparer.CurrentCultureIgnoreCase);

        var single = devices
            .Select(d => new GraylogDeviceOption(d, style.Resolve(d, d.Hostname)))
            .OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase);

        var options = locations.Concat(single).ToList();

        // Unchanged (the usual case for a poll) - leave the list alone so an
        // open drop-down isn't disturbed.
        if (Signature(options) == Signature(DeviceOptions.Skip(1)))
        {
            return;
        }

        _suppressReload = true;
        try
        {
            var selectedKey = SelectedDevice.Key;
            while (DeviceOptions.Count > 1)
            {
                DeviceOptions.RemoveAt(DeviceOptions.Count - 1);
            }

            foreach (var option in options)
            {
                DeviceOptions.Add(option);
            }

            SelectedDevice = DeviceOptions.FirstOrDefault(o => o.Key == selectedKey) ?? DeviceOptions[0];
        }
        finally
        {
            _suppressReload = false;
        }

        _sourceNames.Clear();
    }

    /// <summary>Each choice's identity, label and (for a location) members - equal when nothing worth rebuilding the list for has changed.</summary>
    private static string Signature(IEnumerable<GraylogDeviceOption> options) =>
        string.Join('\n', options.Select(o => o.Key + "|" + o.Label + "|" + string.Join(',', o.Devices.Select(d => d.DeviceId))));

    /// <summary>Selects a device in the Logs tab's device filter (e.g. from "Show logs" elsewhere).</summary>
    public void SelectDevice(int deviceId)
    {
        var option = DeviceOptions.FirstOrDefault(o => o.DeviceId == deviceId);
        if (option is not null)
        {
            SelectedDevice = option;
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _graylog.ConfigurationChanged -= OnConfigurationChanged;

        if (_autoUpdateTimer is not null)
        {
            _autoUpdateTimer.Stop();
            _autoUpdateTimer.Tick -= OnAutoUpdateTick;
        }

        _searchCts?.Cancel();
    }

    private void ClearFilters()
    {
        _suppressReload = true;
        try
        {
            SearchText = null;
            SelectedStream = Streams[0];
            SelectedDevice = DeviceOptions[0];
            SelectedRange = RangeOptions[0];
            SelectedLevel = IsFleet ? GraylogLevelOption.Any : GraylogLevelOption.For(_settings.Current.Graylog.DeviceLogLevel);
        }
        finally
        {
            _suppressReload = false;
        }

        ReloadFromFirstPage();
    }

    private void FilterToDevice(GraylogMessageItemViewModel? item)
    {
        if (item?.DeviceId is { } deviceId)
        {
            SelectDevice(deviceId);
        }
    }

    private void OpenDevice(GraylogMessageItemViewModel? item)
    {
        if (item?.DeviceId is { } deviceId)
        {
            _windows?.ShowDeviceDetail(deviceId);
        }
    }

    /// <summary>
    /// Graylog was switched on or off, or pointed somewhere else, in
    /// Settings: show or hide accordingly and, if the Logs tab is showing,
    /// search again against the new server.
    /// </summary>
    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(IsConfigured));
            UpdateAutoUpdateTimer();

            if (!_graylog.IsConfigured)
            {
                _searchCts?.Cancel();
                return;
            }

            // Only the Logs tab reacts on its own; a device window picks the
            // change up on its next Refresh.
            if (IsFleet && _isActive)
            {
                _hasLoaded = false;
                _ = LoadStreamsAsync();
                _ = LoadAsync(silent: false);
            }
        });
    }

    /// <summary>
    /// Settings are saved for all sorts of reasons (window placement, filter
    /// chips, this tab's own auto-update toggle) - only a change to how
    /// messages are matched or shown (query field, match any address, time
    /// zone) forgets the worked-out addresses and searches again.
    /// </summary>
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(AutoUpdate));
            OnPropertyChanged(nameof(SelectedAutoUpdateInterval));
            UpdateAutoUpdateTimer();

            var key = MatchingKey(_settings.Current.Graylog);
            if (key == _matchingKey)
            {
                return;
            }

            _matchingKey = key;
            _addressesByDevice.Clear();
            _sourceNames.Clear();

            if (IsFleet && _isActive && _hasLoaded && _graylog.IsConfigured)
            {
                _ = LoadAsync(silent: false);
            }
        });
    }

    private static string MatchingKey(GraylogSettings settings) =>
        string.Join('\n', settings.QueryField, settings.MatchAnyAddress, settings.Timezone);

    private void UpdateAutoUpdateTimer()
    {
        if (_autoUpdateTimer is null)
        {
            return;
        }

        var run = _isActive && AutoUpdate && _graylog.IsConfigured;
        if (run && !_autoUpdateTimer.IsEnabled)
        {
            _autoUpdateTimer.Start();
        }
        else if (!run && _autoUpdateTimer.IsEnabled)
        {
            _autoUpdateTimer.Stop();
        }

        OnPropertyChanged(nameof(LastUpdatedText));
    }

    /// <summary>Only page 1 auto-updates - newer messages arriving would otherwise shift an older page out from under whoever's reading it.</summary>
    private void OnAutoUpdateTick(object? sender, EventArgs e)
    {
        if (!_isActive || !_hasLoaded || IsLoading || _isUpdating || Page != 1 || !_graylog.IsConfigured)
        {
            return;
        }

        _ = LoadAsync(silent: true);
    }

    private void ReloadFromFirstPage()
    {
        if (_suppressReload || !_hasStarted)
        {
            return;
        }

        _page = 1;
        OnPropertyChanged(nameof(Page));
        _ = LoadAsync(silent: false);
    }

    private Task GoToPageAsync(int page)
    {
        if (page < 1 || page > PageCount)
        {
            return Task.CompletedTask;
        }

        _page = page;
        OnPropertyChanged(nameof(Page));
        return LoadAsync(silent: false);
    }

    private async Task LoadStreamsAsync()
    {
        try
        {
            var streams = await _graylog.GetStreamsAsync(_ownerToken).ConfigureAwait(true);

            _suppressReload = true;
            try
            {
                var selectedId = SelectedStream.Id;
                while (Streams.Count > 1)
                {
                    Streams.RemoveAt(Streams.Count - 1);
                }

                foreach (var stream in streams.Where(s => !s.Disabled).OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase))
                {
                    Streams.Add(new GraylogStreamOption(stream.Id, stream.DisplayText));
                }

                SelectedStream = Streams.FirstOrDefault(s => s.Id == selectedId) ?? Streams[0];
            }
            finally
            {
                _suppressReload = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (GraylogApiException ex)
        {
            // Not fatal - searching every stream still works without the list.
            _logger.LogWarning(ex, "Could not load Graylog streams");
        }
    }

    /// <summary>
    /// Searches with the current filters and page. A silent search is an
    /// auto-update: no loading overlay, the selected message stays selected
    /// if it's still on the page, and a failure keeps what's already shown
    /// (saying so beside the toggle) rather than emptying the list every
    /// time Graylog has a blip.
    /// </summary>
    private async Task LoadAsync(bool silent)
    {
        // A user-driven search always wins; an auto-update never interrupts one.
        if (silent && (IsLoading || _isUpdating))
        {
            return;
        }

        _searchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ownerToken);
        _searchCts = cts;

        _hasStarted = true;
        if (silent)
        {
            _isUpdating = true;
        }
        else
        {
            IsLoading = true;
            ErrorMessage = null;
        }

        try
        {
            List<string>? addresses = null;
            var devices = CurrentDevices();

            if (!IsFleet && devices.Count == 0)
            {
                // Without an address, the query would match every device's
                // messages - never show those as this device's.
                ErrorMessage = "This device's details haven't loaded yet, so there's nothing to search Graylog for. Try Refresh in a moment.";
                return;
            }

            if (devices.Count > 0)
            {
                // Worked out side by side - each may wait on a DNS lookup.
                var perDevice = await Task.WhenAll(devices.Select(d => GetAddressesAsync(d, cts.Token))).ConfigureAwait(true);
                addresses = perDevice.SelectMany(a => a).Distinct(StringComparer.Ordinal).ToList();
            }

            var options = _settings.Current.Graylog;
            var query = GraylogQuery.WithMaxLevel(
                GraylogQuery.BuildSimpleQuery(SearchText, options.QueryField, addresses),
                SelectedLevel.Level);

            // A big location's addresses can make a query longer than Graylog
            // (or a proxy in front of it) accepts in a URL - say so plainly
            // rather than failing with an obscure HTTP error.
            if (query.Length > MaxQueryLength)
            {
                ErrorMessage = devices.Count > 1
                    ? $"{SelectedDevice.LocationName ?? "This location"} has too many devices to search Graylog for in one go. Pick one of its devices instead."
                    : "This device has too many addresses to search Graylog for in one go. Turning off \"Match any address\" in Settings, Integrations, Graylog searches its main addresses only.";
                _hasLoaded = true;
                Messages.Clear();
                TotalResults = 0;
                return;
            }

            var result = await _graylog.SearchAsync(
                query,
                SelectedRange.Seconds,
                SelectedPageSize,
                (Page - 1) * SelectedPageSize,
                NewestFirst,
                GraylogQuery.StreamFilter(SelectedStream.Id),
                cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            var zone = GraylogQuery.FindTimeZone(options.Timezone);
            var selectedId = SelectedMessage?.Id;

            SelectedMessage = null;
            Messages.Clear();
            foreach (var envelope in result.Messages)
            {
                Messages.Add(new GraylogMessageItemViewModel(envelope, zone, DeviceFor));
            }

            if (silent && selectedId is not null)
            {
                SelectedMessage = Messages.FirstOrDefault(m => m.Id == selectedId);
            }

            _hasLoaded = true;
            _lastUpdated = DateTimeOffset.Now;
            _updateErrorMessage = null;
            ErrorMessage = null;
            TotalResults = result.TotalResults;
            RaisePagingChanged();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search, or the owner closed.
        }
        catch (GraylogApiException ex)
        {
            _logger.LogWarning(ex, "Could not search Graylog");

            if (silent && _hasLoaded)
            {
                _updateErrorMessage = ex.ToUserMessage();
            }
            else
            {
                _hasLoaded = true;
                Messages.Clear();
                TotalResults = 0;
                ErrorMessage = ex.ToUserMessage();
            }
        }
        finally
        {
            if (silent)
            {
                _isUpdating = false;
            }

            // Only the newest search owns the loading state. Each search
            // disposes its own token source once it's done with it - a newer
            // search only ever cancels the one still in _searchCts.
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                IsLoading = false;
                OnPropertyChanged(nameof(ShowEmptyMessage));
                RaisePagingChanged();
            }

            OnPropertyChanged(nameof(LastUpdatedText));
            OnPropertyChanged(nameof(HasUpdateError));
            cts.Dispose();
        }
    }

    /// <summary>The fixed device, or the devices the Logs tab's device filter covers (none for all devices).</summary>
    private IReadOnlyList<Device> CurrentDevices()
    {
        if (_fixedDevice is not null)
        {
            return _fixedDevice() is { } device ? new[] { device } : Array.Empty<Device>();
        }

        return SelectedDevice.Devices;
    }

    /// <summary>
    /// A device's addresses for the query field (see
    /// <see cref="GraylogQuery.DeviceAddresses"/>), worked out once per
    /// device until settings change.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetAddressesAsync(Device device, CancellationToken cancellationToken)
    {
        if (_addressesByDevice.TryGetValue(device.DeviceId, out var cached))
        {
            return cached;
        }

        var resolved = await ResolveHostnameAsync(device.Hostname, cancellationToken).ConfigureAwait(true);

        IReadOnlyList<DeviceIpAddress>? interfaceAddresses = null;
        if (_settings.Current.Graylog.MatchAnyAddress)
        {
            try
            {
                interfaceAddresses = await _client.Ports.ListIpAddressesAsync(device.DeviceId, cancellationToken).ConfigureAwait(true);
            }
            catch (LibreNmsApiException ex)
            {
                // Still worth searching by the primary addresses alone.
                _logger.LogWarning(ex, "Could not load IP addresses for device {DeviceId}; Graylog will match its primary addresses only", device.DeviceId);
            }
        }

        var addresses = GraylogQuery.DeviceAddresses(device, resolved, interfaceAddresses);
        _addressesByDevice[device.DeviceId] = addresses;
        return addresses;
    }

    /// <summary>LibreNMS's <c>gethostbyname($device->hostname)</c> - the first IPv4 address, or null if it doesn't resolve in time.</summary>
    private static async Task<string?> ResolveHostnameAsync(string? hostname, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        if (IPAddress.TryParse(hostname, out var literal))
        {
            return literal.AddressFamily == AddressFamily.InterNetwork ? literal.ToString() : null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HostnameLookupTimeout);

            var addresses = await Dns.GetHostAddressesAsync(hostname, AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(true);
            return addresses.FirstOrDefault()?.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>A known device's name and id for a message's source or origin address, or the address itself.</summary>
    private (string Name, int? DeviceId) DeviceFor(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return (string.Empty, null);
        }

        if (_sourceNames.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var device = _deviceCache.FindByAddress(address)
                     ?? DeviceOptions.Select(o => o.Device).FirstOrDefault(d =>
                         d is not null
                         && (string.Equals(d.Ip, address, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(d.Hostname, address, StringComparison.OrdinalIgnoreCase)));

        var result = device is null
            ? (address, (int?)null)
            : (_settings.Current.DeviceNameStyle.Resolve(device, device.Hostname), device.DeviceId);

        _sourceNames[address] = result;
        return result;
    }

    private void RaisePagingChanged()
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageSummaryText));
        OnPropertyChanged(nameof(LastUpdatedText));
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>One Graylog message row - LibreNMS's columns: level colour, Origin, Timestamp, Level, Source, Message, Facility.</summary>
public sealed class GraylogMessageItemViewModel
{
    public GraylogMessageItemViewModel(GraylogMessageEnvelope envelope, TimeZoneInfo? zone, Func<string?, (string Name, int? DeviceId)> device)
    {
        var message = envelope.Message;

        Id = message.Id;
        Level = message.Level;
        LevelText = GraylogQuery.LevelText(message.Level);
        TimestampText = message.Timestamp is { } timestamp ? GraylogQuery.FormatTimestamp(timestamp, zone) : string.Empty;

        var origin = device(message.RemoteIp);
        var source = device(message.Source);
        OriginText = origin.Name;
        SourceText = source.Name;

        // The device the message is about: its source, or failing that the
        // address it arrived from.
        DeviceId = source.DeviceId ?? origin.DeviceId;

        MessageText = (message.Text ?? string.Empty).ReplaceLineEndings(" ");
        FullText = message.FullMessage ?? message.Text ?? string.Empty;
        FacilityText = GraylogQuery.FacilityText(message.Facility);

        Fields = message.Fields
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Select(f => new GraylogFieldViewModel(f.Key, message.GetText(f.Key) ?? string.Empty))
            .ToList();
    }

    public string? Id { get; }

    public int? Level { get; }

    /// <summary>LibreNMS's colour bands: 0-3 danger, 4 warning, 5-6 info, 7 muted; no level counts as info.</summary>
    public string Severity => Level switch
    {
        >= 0 and <= 3 => "Critical",
        4 => "Warning",
        7 => "Debug",
        _ => "Info",
    };

    public string LevelText { get; }

    public string TimestampText { get; }

    public string OriginText { get; }

    public string SourceText { get; }

    /// <summary>The LibreNMS device this message came from, when its source or origin matches one.</summary>
    public int? DeviceId { get; }

    public bool HasDevice => DeviceId is not null;

    /// <summary>On one line for the grid; <see cref="FullText"/> keeps any line breaks for the details pane.</summary>
    public string MessageText { get; }

    public string FullText { get; }

    public string FacilityText { get; }

    public IReadOnlyList<GraylogFieldViewModel> Fields { get; }
}

public sealed record GraylogFieldViewModel(string Name, string Value);

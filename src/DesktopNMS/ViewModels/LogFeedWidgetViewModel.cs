using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Logs;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// What the Event log and Graylog dashboard widgets (#202, #203) share: the
/// newest entries as two-line rows, refreshed with each device poll and the
/// Dashboard's refresh button; a count, a search and (per widget) a filter
/// in the title bar, saved per widget; and a brief tint on entries that are
/// new since the last refresh. If a refresh fails, the last rows stay under
/// the error.
/// </summary>
public abstract class LogFeedWidgetViewModel : DashboardWidgetViewModel, IDisposable
{
    public static readonly IReadOnlyList<int> CountChoices = new[] { 10, 25, 50 };

    public const int DefaultCount = 10;

    /// <summary>How long a new entry stays tinted.</summary>
    private static readonly TimeSpan NewHighlight = TimeSpan.FromSeconds(4);

    private readonly IDashboardLayoutService _layout;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _newTimer;
    private readonly HashSet<string> _shownKeys = new(StringComparer.Ordinal);

    private int _count;
    private string? _searchText;
    private bool _isSearchOpen;
    private bool _isLoading;
    private bool _hasLoaded;
    private string? _errorMessage;
    private int _loadVersion;

    protected LogFeedWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, DeviceMonitor deviceMonitor, ILogger logger)
        : base(layout, model)
    {
        _layout = layout;
        _deviceMonitor = deviceMonitor;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _count = CountChoices.Contains(model.LogCount) ? model.LogCount : DefaultCount;
        _searchText = model.LogSearch;
        _isSearchOpen = !string.IsNullOrWhiteSpace(model.LogSearch);
        LogType = model.LogType;
        GraylogStreamId = model.GraylogStreamId;
        GraylogRangeSeconds = model.GraylogRangeSeconds;

        Rows = new BatchObservableCollection<LogFeedRowViewModel>();
        Header = new LogFeedHeader(this);
        ToggleSearchCommand = new RelayCommand(() => IsSearchOpen = !IsSearchOpen);
        OpenCommand = new RelayCommand(parameter =>
        {
            if (parameter is LogFeedRowViewModel row)
            {
                Open(row);
            }
        });

        _newTimer = new DispatcherTimer { Interval = NewHighlight };
        _newTimer.Tick += (_, _) =>
        {
            _newTimer.Stop();
            foreach (var row in Rows)
            {
                row.IsNew = false;
            }
        };

        // Refresh with each device poll, like the Top widgets. DeviceMonitor
        // is lazily started, and this may be the only thing asking for it.
        _deviceMonitor.Polled += OnDevicesPolled;
        _deviceMonitor.Start();
    }

    public BatchObservableCollection<LogFeedRowViewModel> Rows { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand ToggleSearchCommand { get; }

    /// <summary>The title bar's controls - see <see cref="LogFeedHeader"/>.</summary>
    public override object? HeaderOptions => Header;

    public LogFeedHeader Header { get; }

    /// <summary>The Event log's type filter in the title bar.</summary>
    public virtual bool OffersTypeFilter => false;

    /// <summary>Graylog's stream and time range in the title bar.</summary>
    public virtual bool OffersStream => false;

    /// <summary>The search box's hint.</summary>
    public abstract string SearchHint { get; }

    /// <summary>"No events." - when nothing matches, or there's nothing at all.</summary>
    public abstract string EmptyText { get; }

    public int SelectedCount
    {
        get => _count;
        set
        {
            if (CountChoices.Contains(value) && SetProperty(ref _count, value))
            {
                SaveOptionsAndReload();
            }
        }
    }

    public string? SearchText
    {
        get => _searchText;
        set
        {
            var normalised = string.IsNullOrWhiteSpace(value) ? null : value;
            if (SetProperty(ref _searchText, normalised))
            {
                OnPropertyChanged(nameof(HasSearch));
                OnPropertyChanged(nameof(EmptyText));
                SaveOptionsAndReload();
            }
        }
    }

    public bool HasSearch => !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>The search row under the title bar - open while there's a search, or once the magnifier is clicked.</summary>
    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set
        {
            if (SetProperty(ref _isSearchOpen, value) && !value && HasSearch)
            {
                // Closing the box clears what's in it - a hidden search would be a trap.
                SearchText = null;
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseStateChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                RaiseStateChanged();
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public bool HasRows => Rows.Count > 0;

    /// <summary>Only before the first result - later refreshes update in place.</summary>
    public bool ShowLoading => IsLoading && !_hasLoaded;

    public bool ShowEmptyMessage => _hasLoaded && !IsLoading && !HasRows && !HasError;

    protected string? LogType { get; set; }

    protected string? GraylogStreamId { get; set; }

    protected int GraylogRangeSeconds { get; set; }

    protected int Count => _count;

    /// <summary>The Dashboard's refresh button: fetch again.</summary>
    public void Reload() => _ = LoadAsync();

    /// <summary>The newest rows, at most <see cref="Count"/>, with whatever filters are set.</summary>
    protected abstract Task<IReadOnlyList<LogFeedRowViewModel>> FetchAsync();

    protected abstract void Open(LogFeedRowViewModel row);

    /// <summary>A failure worth showing (a reachable server saying no, or no answer), or null to rethrow.</summary>
    protected abstract string? DescribeFailure(Exception exception);

    protected void SaveOptionsAndReload()
    {
        _layout.SetLogOptions(Id, _count, LogType, _searchText, GraylogStreamId, GraylogRangeSeconds);

        // A different filter is a different list: nothing in it is "new".
        _shownKeys.Clear();
        _ = LoadAsync();
    }

    /// <summary>"14:05" today, "Thu 14:05" this week, "28 Sep 14:05" before that.</summary>
    protected static string FormatTime(DateTime local)
    {
        var today = DateTime.Today;
        if (local.Date == today)
        {
            return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        return local.Date > today.AddDays(-7)
            ? local.ToString("ddd HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }

    protected async Task LoadAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true;

        try
        {
            var rows = await FetchAsync().ConfigureAwait(true);
            if (version != _loadVersion)
            {
                return;
            }

            ErrorMessage = null;
            Apply(rows);
        }
        catch (Exception ex) when (DescribeFailure(ex) is { } message)
        {
            _logger.LogWarning(ex, "Could not load {Widget}", Title);
            if (version == _loadVersion)
            {
                // The last rows stay on screen under the error.
                ErrorMessage = message;
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                _hasLoaded = true;
                IsLoading = false;
            }
        }
    }

    private void Apply(IReadOnlyList<LogFeedRowViewModel> rows)
    {
        // Tint what wasn't there last time - but only when some of last time's
        // rows are still showing. On the first load, or when a busy Graylog
        // has replaced the whole list since the last refresh, everything is
        // "new" and tinting it all would say nothing.
        var highlight = rows.Any(r => _shownKeys.Contains(r.Key));
        var anyNew = false;
        foreach (var row in rows)
        {
            if (highlight && !_shownKeys.Contains(row.Key))
            {
                row.IsNew = true;
                anyNew = true;
            }
        }

        _shownKeys.Clear();
        _shownKeys.UnionWith(rows.Select(r => r.Key));

        using (Rows.BeginBatch())
        {
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }
        }

        if (anyNew)
        {
            _newTimer.Stop();
            _newTimer.Start();
        }

        RaiseStateChanged();
    }

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (result.Succeeded)
        {
            _dispatcher.InvokeAsync(() => _ = LoadAsync());
        }
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowEmptyMessage));
    }

    public virtual void Dispose()
    {
        _deviceMonitor.Polled -= OnDevicesPolled;
        _newTimer.Stop();
    }
}

/// <summary>
/// What a log feed widget puts in its title bar (#202, #203): the Event
/// log's type, or Graylog's stream and time range; the search magnifier;
/// and the count. Its own type so the title bar can template it.
/// </summary>
public sealed class LogFeedHeader
{
    public LogFeedHeader(LogFeedWidgetViewModel widget) => Widget = widget;

    public LogFeedWidgetViewModel Widget { get; }

    public IReadOnlyList<int> CountChoices => LogFeedWidgetViewModel.CountChoices;
}

/// <summary>One choice in a log feed widget's title-bar dropdown: a type, a stream or a time range.</summary>
public sealed record LogFeedChoice(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row: the time, who it's about, a tag (the event type or Graylog level), and the message.</summary>
public sealed class LogFeedRowViewModel : ObservableObject
{
    private bool _isNew;

    public LogFeedRowViewModel(string key, string timeText, string fullTimeText, string title, string? tag, string message, int? deviceId, AlertSeverity? severity)
    {
        Key = key;
        TimeText = timeText;
        FullTimeText = fullTimeText;
        Title = title;
        Tag = string.IsNullOrWhiteSpace(tag) ? null : tag;
        Message = message;
        DeviceId = deviceId;
        Severity = severity;
    }

    /// <summary>Identifies the entry across refreshes, for the "new" tint.</summary>
    public string Key { get; }

    public string TimeText { get; }

    /// <summary>The full date and time, for the tooltip.</summary>
    public string FullTimeText { get; }

    public string Title { get; }

    public string? Tag { get; }

    public bool HasTag => Tag is not null;

    public string Message { get; }

    public int? DeviceId { get; }

    /// <summary>Graylog's level as a dot colour; null for no dot.</summary>
    public AlertSeverity? Severity { get; }

    public bool HasSeverity => Severity is not null;

    /// <summary>Arrived since the last refresh - tinted for a few seconds.</summary>
    public bool IsNew
    {
        get => _isNew;
        set => SetProperty(ref _isNew, value);
    }
}

/// <summary>Event log (#202): the newest LibreNMS events across every device, by type and text.</summary>
public sealed class EventLogWidgetViewModel : LogFeedWidgetViewModel
{
    private const string AllTypesLabel = "All types";

    private readonly ILibreNmsClient _client;
    private readonly IDeviceCache _devices;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private LogFeedChoice _selectedType;

    public EventLogWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, ILibreNmsClient client, IDeviceCache devices, ISettingsStore settings, DeviceMonitor deviceMonitor, IWindowService windows, ILogger logger)
        : base(layout, model, deviceMonitor, logger)
    {
        _client = client;
        _devices = devices;
        _settings = settings;
        _windows = windows;

        TypeChoices = new ObservableCollection<LogFeedChoice> { new(null, AllTypesLabel) };
        if (!string.IsNullOrWhiteSpace(LogType))
        {
            TypeChoices.Add(new LogFeedChoice(LogType, LogType));
        }

        _selectedType = TypeChoices.Last();
        _ = LoadAsync();
    }

    public override bool OffersTypeFilter => true;

    public override string SearchHint => "Search events";

    public override string EmptyText => string.IsNullOrWhiteSpace(LogType) && !HasSearch ? "No events yet." : "No events match.";

    /// <summary>"All types", then every type seen so far.</summary>
    public ObservableCollection<LogFeedChoice> TypeChoices { get; }

    public LogFeedChoice SelectedType
    {
        get => _selectedType;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedType))
            {
                return;
            }

            _selectedType = value;
            OnPropertyChanged();
            LogType = value.Value;
            OnPropertyChanged(nameof(EmptyText));
            SaveOptionsAndReload();
        }
    }

    protected override async Task<IReadOnlyList<LogFeedRowViewModel>> FetchAsync()
    {
        var limit = EventLogFeed.FetchLimit(Count, LogType, SearchText);
        var entries = await _client.Logs.ListEventLogAsync(null, limit).ConfigureAwait(true);
        AddTypes(EventLogFeed.Types(entries));

        var utc = _settings.Current.ServerTimestampsAreUtc;
        return EventLogFeed.Filter(entries, LogType, SearchText, Count)
            .Select(e =>
            {
                var local = ServerTime.ToLocal(e.Timestamp, utc);
                return new LogFeedRowViewModel(
                    "event:" + e.Id.ToString(CultureInfo.InvariantCulture),
                    local is { } l ? FormatTime(l) : string.Empty,
                    local is { } f ? f.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.CurrentCulture) : string.Empty,
                    DeviceName(e),
                    e.Type?.Trim(),
                    (e.Message ?? string.Empty).ReplaceLineEndings(" "),
                    e.DeviceId > 0 ? e.DeviceId : null,
                    severity: null);
            })
            .ToList();
    }

    protected override void Open(LogFeedRowViewModel row)
    {
        if (row.DeviceId is { } deviceId)
        {
            _windows.ShowDeviceEventLog(deviceId);
        }
    }

    protected override string? DescribeFailure(Exception exception)
        => exception is LibreNmsApiException ? "Couldn't reach LibreNMS for the event log." : null;

    private string DeviceName(EventLogEntry entry)
    {
        if (_devices.Get(entry.DeviceId) is { } device)
        {
            return _settings.Current.DeviceNameStyle.Resolve(device, device.Hostname);
        }

        return entry.SysName is { Length: > 0 } && _settings.Current.DeviceNameStyle != DeviceNameStyle.Hostname
            ? entry.SysName
            : entry.Hostname ?? (entry.DeviceId > 0 ? "Device " + entry.DeviceId.ToString(CultureInfo.CurrentCulture) : "LibreNMS");
    }

    /// <summary>Types only ever join the list, so the dropdown doesn't shift under the pointer between refreshes.</summary>
    private void AddTypes(IReadOnlyList<string> types)
    {
        foreach (var type in types)
        {
            if (!TypeChoices.Any(c => string.Equals(c.Value, type, StringComparison.OrdinalIgnoreCase)))
            {
                var index = 1;
                while (index < TypeChoices.Count && string.Compare(TypeChoices[index].Label, type, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    index++;
                }

                TypeChoices.Insert(index, new LogFeedChoice(type, type));
            }
        }
    }
}

/// <summary>Graylog (#203): the newest Graylog messages, by stream, time range and search.</summary>
public sealed class GraylogWidgetViewModel : LogFeedWidgetViewModel
{
    private const string AllStreamsLabel = "All streams";

    public static readonly IReadOnlyList<LogFeedChoice> RangeChoices = new[]
    {
        new LogFeedChoice("300", "5 min"),
        new LogFeedChoice("900", "15 min"),
        new LogFeedChoice("3600", "60 min"),
    };

    private readonly IGraylogApi _graylog;
    private readonly IDeviceCache _devices;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private LogFeedChoice _selectedStream;
    private LogFeedChoice _selectedRange;
    private bool _streamsLoaded;

    public GraylogWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, IGraylogApi graylog, IDeviceCache devices, ISettingsStore settings, DeviceMonitor deviceMonitor, IWindowService windows, ILogger logger)
        : base(layout, model, deviceMonitor, logger)
    {
        _graylog = graylog;
        _devices = devices;
        _settings = settings;
        _windows = windows;

        StreamChoices = new ObservableCollection<LogFeedChoice> { new(null, AllStreamsLabel) };
        if (!string.IsNullOrWhiteSpace(GraylogStreamId))
        {
            // Named properly once the streams have loaded.
            StreamChoices.Add(new LogFeedChoice(GraylogStreamId, "Stream"));
        }

        _selectedStream = StreamChoices.Last();
        _selectedRange = RangeChoices.FirstOrDefault(r => r.Value == GraylogRangeSeconds.ToString(CultureInfo.InvariantCulture)) ?? RangeChoices[1];
        GraylogRangeSeconds = int.Parse(_selectedRange.Value!, CultureInfo.InvariantCulture);

        _graylog.ConfigurationChanged += OnGraylogConfigurationChanged;
        _ = LoadAsync();
    }

    public override bool OffersStream => true;

    public override string SearchHint => "Graylog search, e.g. level:<=3";

    /// <summary>With Graylog switched off, say how to fix it rather than showing an error (or old messages).</summary>
    public override string EmptyText => _graylog.IsConfigured
        ? "No messages in the last " + _selectedRange.Label + "."
        : "Set up Graylog in Settings first.";

    /// <summary>"All streams", then each stream the account can read.</summary>
    public ObservableCollection<LogFeedChoice> StreamChoices { get; }

    public IReadOnlyList<LogFeedChoice> Ranges => RangeChoices;

    public LogFeedChoice SelectedStream
    {
        get => _selectedStream;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedStream))
            {
                return;
            }

            _selectedStream = value;
            OnPropertyChanged();
            GraylogStreamId = value.Value;
            SaveOptionsAndReload();
        }
    }

    public LogFeedChoice SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedRange))
            {
                return;
            }

            _selectedRange = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmptyText));
            GraylogRangeSeconds = int.Parse(value.Value!, CultureInfo.InvariantCulture);
            SaveOptionsAndReload();
        }
    }

    protected override async Task<IReadOnlyList<LogFeedRowViewModel>> FetchAsync()
    {
        if (!_graylog.IsConfigured)
        {
            return Array.Empty<LogFeedRowViewModel>();
        }

        if (!_streamsLoaded)
        {
            await LoadStreamsAsync().ConfigureAwait(true);
        }

        var query = string.IsNullOrWhiteSpace(SearchText) ? "*" : SearchText.Trim();
        var result = await _graylog.SearchAsync(query, GraylogRangeSeconds, Count, 0, "timestamp:desc", GraylogQuery.StreamFilter(GraylogStreamId)).ConfigureAwait(true);
        var zone = GraylogQuery.FindTimeZone(_settings.Current.Graylog.Timezone);

        return result.Messages
            .Select((envelope, index) =>
            {
                var message = envelope.Message;
                var device = _devices.FindByAddress(message.Source) ?? _devices.FindByAddress(message.RemoteIp);
                var shown = message.Timestamp is { } t ? (zone is null ? t.ToLocalTime() : TimeZoneInfo.ConvertTime(t, zone)) : (DateTimeOffset?)null;

                return new LogFeedRowViewModel(
                    "graylog:" + (message.Id ?? index.ToString(CultureInfo.InvariantCulture) + (message.Text ?? string.Empty)),
                    shown is { } s ? FormatTime(s.DateTime) : string.Empty,
                    message.Timestamp is { } full ? GraylogQuery.FormatTimestamp(full, zone) : string.Empty,
                    device is not null
                        ? _settings.Current.DeviceNameStyle.Resolve(device, device.Hostname)
                        : message.Source ?? message.RemoteIp ?? "Unknown source",
                    LevelName(message.Level),
                    (message.Text ?? string.Empty).ReplaceLineEndings(" "),
                    device?.DeviceId,
                    LevelSeverity(message.Level));
            })
            .ToList();
    }

    protected override void Open(LogFeedRowViewModel row)
    {
        if (row.DeviceId is { } deviceId)
        {
            _windows.ShowDeviceGraylog(deviceId);
        }
        else
        {
            _windows.ShowLogsTab();
        }
    }

    protected override string? DescribeFailure(Exception exception) => exception switch
    {
        GraylogApiException => "Couldn't reach Graylog.",
        _ => null,
    };

    /// <summary>Syslog's short names: 0 emerg … 7 debug.</summary>
    private static string? LevelName(int? level) => level switch
    {
        0 => "emerg",
        1 => "alert",
        2 => "crit",
        3 => "err",
        4 => "warning",
        5 => "notice",
        6 => "info",
        7 => "debug",
        _ => null,
    };

    /// <summary>The Logs tab's colours: red for error and worse, amber for warning, grey otherwise.</summary>
    private static AlertSeverity? LevelSeverity(int? level) => level switch
    {
        >= 0 and <= 3 => AlertSeverity.Critical,
        4 => AlertSeverity.Warning,
        _ => AlertSeverity.Unknown,
    };

    private async Task LoadStreamsAsync()
    {
        try
        {
            var streams = await _graylog.GetStreamsAsync().ConfigureAwait(true);
            _streamsLoaded = true;

            var selectedId = _selectedStream.Value;
            while (StreamChoices.Count > 1)
            {
                StreamChoices.RemoveAt(StreamChoices.Count - 1);
            }

            foreach (var stream in streams.Where(s => !s.Disabled).OrderBy(s => s.Title ?? s.Id, StringComparer.CurrentCultureIgnoreCase))
            {
                StreamChoices.Add(new LogFeedChoice(stream.Id, string.IsNullOrWhiteSpace(stream.Title) ? stream.Id : stream.Title!));
            }

            // Keep the saved stream even if this account can't see it any more,
            // rather than quietly switching to every stream.
            var match = StreamChoices.FirstOrDefault(c => c.Value == selectedId);
            if (match is null)
            {
                match = new LogFeedChoice(selectedId, "Unknown stream");
                StreamChoices.Add(match);
            }

            _selectedStream = match;
            OnPropertyChanged(nameof(SelectedStream));
        }
        catch (GraylogApiException)
        {
            // The search below reports the problem; try the streams again next time.
        }
    }

    private void OnGraylogConfigurationChanged(object? sender, EventArgs e)
    {
        _streamsLoaded = false;
        OnPropertyChanged(nameof(EmptyText));
        _ = LoadAsync();
    }

    public override void Dispose()
    {
        _graylog.ConfigurationChanged -= OnGraylogConfigurationChanged;
        base.Dispose();
    }
}

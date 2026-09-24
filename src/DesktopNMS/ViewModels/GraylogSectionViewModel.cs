using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>A syslog level choice - "(4) Warning" - for the Graylog level filter and its default in Settings.</summary>
public sealed record GraylogLevelOption(int Level, string Label)
{
    public static IReadOnlyList<GraylogLevelOption> All { get; } =
        Enumerable.Range(0, 8).Select(l => new GraylogLevelOption(l, GraylogQuery.LevelText(l))).ToList();

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
/// Backs Device Details' Integrations, Graylog tab (issue #114) - this
/// device's Graylog messages, found and filtered the way LibreNMS's own
/// device Graylog tab finds them (see <see cref="GraylogQuery"/>): by stream,
/// highest level, time range and message text, a page at a time, newest
/// first. Nothing is fetched until the tab is first opened (see
/// <see cref="EnsureLoaded"/>), and only when Graylog is set up at all - the
/// tab isn't shown otherwise.
/// </summary>
public sealed class GraylogSectionViewModel : ObservableObject
{
    private const string NewestFirst = "timestamp:desc";

    /// <summary>How long to wait for the hostname lookup LibreNMS does (gethostbyname) before carrying on without it.</summary>
    private static readonly TimeSpan HostnameLookupTimeout = TimeSpan.FromSeconds(3);

    private readonly int _deviceId;
    private readonly Func<Device?> _device;
    private readonly IGraylogApi _graylog;
    private readonly ILibreNmsClient _client;
    private readonly IDeviceCache _deviceCache;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;
    private readonly CancellationToken _windowToken;
    private readonly Dictionary<string, string> _sourceNames = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string>? _addresses;
    private CancellationTokenSource? _searchCts;
    private bool _hasLoaded;
    private bool _hasStarted;
    private bool _suppressReload;

    private GraylogStreamOption _selectedStream;
    private GraylogLevelOption _selectedLevel;
    private GraylogRangeOption _selectedRange;
    private int _selectedPageSize;
    private string? _searchText;
    private int _page = 1;
    private long _totalResults;
    private bool _isLoading;
    private string? _errorMessage;
    private GraylogMessageItemViewModel? _selectedMessage;

    public GraylogSectionViewModel(
        int deviceId,
        Func<Device?> device,
        IGraylogApi graylog,
        ILibreNmsClient client,
        IDeviceCache deviceCache,
        ISettingsStore settings,
        ILogger logger,
        CancellationToken windowToken)
    {
        _deviceId = deviceId;
        _device = device;
        _graylog = graylog;
        _client = client;
        _deviceCache = deviceCache;
        _settings = settings;
        _logger = logger;
        _windowToken = windowToken;

        var options = settings.Current.Graylog;

        Streams = new ObservableCollection<GraylogStreamOption> { new(null, "All streams") };
        _selectedStream = Streams[0];

        _selectedLevel = GraylogLevelOption.For(options.DeviceLogLevel);

        RangeOptions = GraylogQuery.Ranges.Select(r => new GraylogRangeOption(r.Seconds, r.Label)).ToList();
        _selectedRange = RangeOptions[0];

        // The configured row count (LibreNMS's device-page rowCount) first,
        // then LibreNMS's own fixed page sizes.
        var rows = Math.Clamp(options.DeviceRowCount, 1, GraylogSettings.MaxRowCount);
        PageSizeOptions = new[] { rows, 25, 50, 100, 250 }.Distinct().OrderBy(n => n).ToList();
        _selectedPageSize = rows;

        Messages = new ObservableCollection<GraylogMessageItemViewModel>();
        SelectedMessageFields = Array.Empty<GraylogFieldViewModel>();

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        PreviousPageCommand = new AsyncRelayCommand(() => GoToPageAsync(Page - 1), () => !IsLoading && Page > 1);
        NextPageCommand = new AsyncRelayCommand(() => GoToPageAsync(Page + 1), () => !IsLoading && Page < PageCount);
    }

    /// <summary>Live, so turning Graylog on or off in Settings shows or hides the tab in windows opened afterwards (and this one's tab on its next check).</summary>
    public bool IsConfigured => _graylog.IsConfigured;

    public ObservableCollection<GraylogStreamOption> Streams { get; }

    public IReadOnlyList<GraylogLevelOption> LevelOptions => GraylogLevelOption.All;

    public IReadOnlyList<GraylogRangeOption> RangeOptions { get; }

    public IReadOnlyList<int> PageSizeOptions { get; }

    public ObservableCollection<GraylogMessageItemViewModel> Messages { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PreviousPageCommand { get; }

    public AsyncRelayCommand NextPageCommand { get; }

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

    /// <summary>"1-25 of 1,234 messages", or nothing before the first load.</summary>
    public string PageSummaryText
    {
        get
        {
            if (!_hasLoaded || IsLoading && Messages.Count == 0)
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

    /// <summary>First load, when the tab is first opened - later visits keep what's there (Refresh fetches again).</summary>
    public void EnsureLoaded()
    {
        if (_hasLoaded || IsLoading || !_graylog.IsConfigured)
        {
            return;
        }

        _ = LoadStreamsAsync();
        _ = LoadAsync();
    }

    /// <summary>Device Details' own Refresh - only re-fetches once the tab has actually been opened.</summary>
    public Task RefreshIfLoadedAsync() => _hasLoaded && _graylog.IsConfigured ? LoadAsync() : Task.CompletedTask;

    private void ReloadFromFirstPage()
    {
        if (_suppressReload || !_hasStarted)
        {
            return;
        }

        _page = 1;
        OnPropertyChanged(nameof(Page));
        _ = LoadAsync();
    }

    private Task GoToPageAsync(int page)
    {
        if (page < 1 || page > PageCount)
        {
            return Task.CompletedTask;
        }

        _page = page;
        OnPropertyChanged(nameof(Page));
        return LoadAsync();
    }

    private async Task LoadStreamsAsync()
    {
        try
        {
            var streams = await _graylog.GetStreamsAsync(_windowToken).ConfigureAwait(true);

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
            _logger.LogWarning(ex, "Could not load Graylog streams for device {DeviceId}", _deviceId);
        }
    }

    private async Task LoadAsync()
    {
        _searchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_windowToken);
        _searchCts = cts;

        _hasStarted = true;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var addresses = await GetAddressesAsync(cts.Token).ConfigureAwait(true);
            if (addresses.Count == 0)
            {
                // Without an address, the query would match every device's
                // messages - never show those as this device's.
                ErrorMessage = "This device's details haven't loaded yet, so there's nothing to search Graylog for. Try Refresh in a moment.";
                return;
            }

            var options = _settings.Current.Graylog;
            var query = GraylogQuery.WithMaxLevel(
                GraylogQuery.BuildSimpleQuery(SearchText, options.QueryField, addresses.ToList()),
                SelectedLevel.Level);

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

            SelectedMessage = null;
            Messages.Clear();
            foreach (var envelope in result.Messages)
            {
                Messages.Add(new GraylogMessageItemViewModel(envelope, zone, DeviceNameFor));
            }

            _hasLoaded = true;
            TotalResults = result.TotalResults;
            RaisePagingChanged();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search, or the window closed.
        }
        catch (GraylogApiException ex)
        {
            _logger.LogWarning(ex, "Could not search Graylog for device {DeviceId}", _deviceId);
            _hasLoaded = true;
            Messages.Clear();
            TotalResults = 0;
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
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

            cts.Dispose();
        }
    }

    /// <summary>
    /// The device's addresses for the query field, worked out once per window
    /// (see <see cref="GraylogQuery.DeviceAddresses"/>). Empty, and tried
    /// again next time, while the device itself hasn't loaded.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetAddressesAsync(CancellationToken cancellationToken)
    {
        if (_addresses is not null)
        {
            return _addresses;
        }

        var device = _device();
        if (device is null)
        {
            return Array.Empty<string>();
        }

        var resolved = await ResolveHostnameAsync(device.Hostname, cancellationToken).ConfigureAwait(true);

        IReadOnlyList<DeviceIpAddress>? interfaceAddresses = null;
        if (_settings.Current.Graylog.MatchAnyAddress)
        {
            try
            {
                interfaceAddresses = await _client.Ports.ListIpAddressesAsync(_deviceId, cancellationToken).ConfigureAwait(true);
            }
            catch (LibreNmsApiException ex)
            {
                // Still worth searching by the primary addresses alone.
                _logger.LogWarning(ex, "Could not load IP addresses for device {DeviceId}; Graylog will match its primary addresses only", _deviceId);
            }
        }

        _addresses = GraylogQuery.DeviceAddresses(device, resolved, interfaceAddresses);
        return _addresses;
    }

    /// <summary>LibreNMS's <c>gethostbyname($device->hostname)</c> - the first IPv4 address, or null if it doesn't resolve in time.</summary>
    private async Task<string?> ResolveHostnameAsync(string? hostname, CancellationToken cancellationToken)
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

    /// <summary>A known device's name for a message's source or origin address, or the address itself.</summary>
    private string DeviceNameFor(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        if (_sourceNames.TryGetValue(address, out var cached))
        {
            return cached;
        }

        var device = _deviceCache.FindByAddress(address);
        var name = device is null
            ? address
            : _settings.Current.DeviceNameStyle.Resolve(device, device.Hostname);

        _sourceNames[address] = name;
        return name;
    }

    private void RaisePagingChanged()
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageSummaryText));
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>One Graylog message row - LibreNMS's columns: level colour, Origin, Timestamp, Level, Source, Message, Facility.</summary>
public sealed class GraylogMessageItemViewModel
{
    public GraylogMessageItemViewModel(GraylogMessageEnvelope envelope, TimeZoneInfo? zone, Func<string?, string> deviceName)
    {
        var message = envelope.Message;

        Level = message.Level;
        LevelText = GraylogQuery.LevelText(message.Level);
        TimestampText = message.Timestamp is { } timestamp ? GraylogQuery.FormatTimestamp(timestamp, zone) : string.Empty;
        OriginText = deviceName(message.RemoteIp);
        SourceText = deviceName(message.Source);
        MessageText = (message.Text ?? string.Empty).ReplaceLineEndings(" ");
        FullText = message.FullMessage ?? message.Text ?? string.Empty;
        FacilityText = GraylogQuery.FacilityText(message.Facility);

        Fields = message.Fields
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Select(f => new GraylogFieldViewModel(f.Key, message.GetText(f.Key) ?? string.Empty))
            .ToList();
    }

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

    /// <summary>On one line for the grid; <see cref="FullText"/> keeps any line breaks for the details pane.</summary>
    public string MessageText { get; }

    public string FullText { get; }

    public string FacilityText { get; }

    public IReadOnlyList<GraylogFieldViewModel> Fields { get; }
}

public sealed record GraylogFieldViewModel(string Name, string Value);

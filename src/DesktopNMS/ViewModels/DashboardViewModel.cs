using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// View model behind the Dashboard tab: a free-form canvas of widgets the user
/// can add, drag, resize, rename and remove (see <see cref="Widgets"/> and
/// <see cref="IsEditMode"/>). Widget types: "Sensors" (each showing whichever
/// sensors were added to it specifically, fed by the shared
/// <see cref="SensorMonitor"/> also used by the Health tab), "Alerts" and
/// "AlertsGauge" (fed by the app-wide <see cref="AlertMonitor"/>),
/// "DeviceStatus" (fed by the shared <see cref="DeviceMonitor"/> also used by
/// the Devices tab), "RecentlyViewed" (reads straight from settings, the
/// same list the Devices tab's own recently-viewed strip shows), and
/// "PinnedDevices" (same relationship, but for the Devices tab's pinned/
/// favourite devices), and "Graph" (issue #12 - a configurable per-device
/// graph, reloaded only when this tab is shown or refreshed, since a graph
/// fetch is its own real API call rather than shared poll data), and
/// "Wireless" (#55 - each wireless controller's AP and client counts, asked
/// of just those controllers - see <see cref="WirelessWidgetViewModel"/>). None of the
/// others trigger a fetch of their own - having any combination open never
/// costs more than one poll of each kind of data.
/// </summary>
public sealed class DashboardViewModel : ObservableObject, IDisposable, IWidgetActions
{
    /// <summary>How many steps Undo goes back (#276).</summary>
    private const int MaxUndo = 50;

    /// <summary>Kinds that show nothing until they're set up - the set-up panel opens on them once added (#275).</summary>
    private static readonly HashSet<string> NeedsSetUp = new(StringComparer.Ordinal) { DashboardWidgetTypes.Sensors, DashboardWidgetTypes.Graph };

    private readonly List<IReadOnlyList<DashboardWidget>> _undo = new();
    private IReadOnlyList<DashboardWidget> _current = Array.Empty<DashboardWidget>();
    private IReadOnlyList<DashboardWidget>? _editStart;
    private bool _restoring;
    private DashboardWidgetViewModel? _setUpWidget;
    private DashboardWidgetViewModel? _selectedWidget;
    private string? _toastText;
    private string? _toastWidgetId;
    private DispatcherTimer? _toastTimer;

    private readonly SensorMonitor _sensorMonitor;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly IWindowService _windows;
    private readonly IDashboardLayoutService _layout;
    private readonly AlertMonitor _alertMonitor;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly ILibreNmsClient _client;
    private readonly IFleetPorts _fleetPorts;
    private readonly IGraylogApi _graylog;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DashboardWidgetViewModel> _widgetIndex = new();
    private readonly AutoRefreshTimer _autoRefresh;

    private string _statusMessage = "Not loaded yet.";
    private string? _errorMessage;
    private bool _isBusy;
    private bool _isEditMode;
    private DateTimeOffset? _lastUpdated;
    private bool _hasLoadedOnce;

    /// <summary>The last successful fetch, kept so a brand-new widget can be
    /// seeded instantly (see <see cref="OnLayoutChanged"/>) instead of waiting
    /// for the next poll.</summary>
    private IReadOnlyList<Sensor> _lastFleet = Array.Empty<Sensor>();
    private Func<int, string> _lastDeviceNameFor = id => $"device {id}";

    public DashboardViewModel(
        SensorMonitor sensorMonitor,
        ISessionService session,
        ISettingsStore settings,
        IDeviceCache devices,
        IWindowService windows,
        IDashboardLayoutService layout,
        AlertMonitor alertMonitor,
        DeviceMonitor deviceMonitor,
        ILibreNmsClient client,
        IFleetPorts fleetPorts,
        IGraylogApi graylog,
        IUnimusApi unimus,
        ILogger<DashboardViewModel> logger)
    {
        _fleetPorts = fleetPorts;
        _graylog = graylog;
        _sensorMonitor = sensorMonitor;
        _session = session;
        _settings = settings;
        _devices = devices;
        _windows = windows;
        _layout = layout;
        _alertMonitor = alertMonitor;
        _deviceMonitor = deviceMonitor;
        _client = client;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Widgets = new ObservableCollection<DashboardWidgetViewModel>();

        RefreshCommand = new AsyncRelayCommand(() =>
        {
            _sensorMonitor.RequestRefresh();
            ReloadGraphWidgets();
            return Task.CompletedTask;
        }, () => _session.IsConnected && !IsBusy);

        OpenDeviceCommand = new RelayCommand(parameter =>
        {
            if (parameter is SensorItemViewModel { DeviceUrl: { } url })
            {
                _windows.OpenUrl(url);
            }
        });

        AddWidgetCommand = new RelayCommand(AddWidget);
        Welcome = new WelcomeViewModel(settings, layout, windows, devices, deviceMonitor, alertMonitor, graylog, unimus, AddWidgetCommand);
        Widgets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowWelcome));

        // A widget row (a sensor, an alert) opens Device Details - staying in the app (#212).
        OpenDeviceDetailsCommand = new RelayCommand(parameter =>
        {
            var deviceId = parameter switch
            {
                SensorItemViewModel sensor => sensor.DeviceId,
                AlertItemViewModel alert => alert.DeviceId,
                _ => 0,
            };

            if (deviceId > 0)
            {
                _windows.ShowDeviceDetail(deviceId);
            }
        });

        _autoRefresh = new AutoRefreshTimer(() => OnPropertyChanged(nameof(NextRefreshText)));

        EditCommand = new RelayCommand(() => IsEditMode = true);
        DoneCommand = new RelayCommand(() => IsEditMode = false);
        CancelCommand = new RelayCommand(CancelEditing);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0);
        TidyUpCommand = new RelayCommand(TidyUp, () => Widgets.Count > 0);
        CloseSetUpCommand = new RelayCommand(() => SetUpWidget = null);
        ToastSetUpCommand = new RelayCommand(() =>
        {
            if (_toastWidgetId is { } id && _widgetIndex.TryGetValue(id, out var widget))
            {
                SetUp(widget);
            }

            DismissToast();
        });
        DismissToastCommand = new RelayCommand(DismissToast);

        SyncWidgets();
        _current = _layout.Snapshot();

        _settings.Changed += OnSettingsChanged;
        _layout.Changed += OnLayoutChanged;
        _session.StateChanged += OnSessionStateChanged;
        _sensorMonitor.PollStarted += OnPollStarted;
        _sensorMonitor.Polled += OnPolled;
    }

    /// <summary>The widgets on the canvas, in the order they were created.</summary>
    public ObservableCollection<DashboardWidgetViewModel> Widgets { get; }

    /// <summary>The empty Dashboard's welcome card (#233).</summary>
    public WelcomeViewModel Welcome { get; }

    /// <summary>Signed in, no widgets yet, and not turned off with "Don't show again".</summary>
    public bool ShowWelcome => _session.IsConnected && Widgets.Count == 0 && !_settings.Current.WelcomeDismissed;

    /// <summary>"Add widget": the picker (#204), then the chosen widget is added, scrolled to and briefly highlighted.</summary>
    public RelayCommand AddWidgetCommand { get; }

    /// <summary>Opens Device Details for a widget row's device.</summary>
    public RelayCommand OpenDeviceDetailsCommand { get; }

    /// <summary>Raised with a just-added widget, so the view can scroll it into sight.</summary>
    public event EventHandler<DashboardWidgetViewModel>? WidgetAdded;

    /// <summary>
    /// "Edit dashboard" (#274): the one edit mode - the edit bar, ⠿ to drag,
    /// corners to resize, the "+" space. Cancel puts the dashboard back as it
    /// was when editing began; Done keeps it.
    /// </summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            if (!SetProperty(ref _isEditMode, value))
            {
                return;
            }

            _editStart = value ? _layout.Snapshot() : null;
            if (!value)
            {
                SelectedWidget = null;
                foreach (var widget in Widgets)
                {
                    widget.IsRenaming = false;
                }
            }

            OnPropertyChanged(nameof(ShowAddSpace));
        }
    }

    public RelayCommand EditCommand { get; }

    public RelayCommand DoneCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Undo (#276): back one change - a move, resize, removal, rename, set-up change, Duplicate or Tidy up. Ctrl+Z too.</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>Tidy up (#278): every widget up as far as it goes, closing the gaps.</summary>
    public RelayCommand TidyUpCommand { get; }

    public RelayCommand CloseSetUpCommand { get; }

    public RelayCommand ToastSetUpCommand { get; }

    public RelayCommand DismissToastCommand { get; }

    /// <summary>"Undo" with how many steps there are to go back - the edit bar's tooltip.</summary>
    public string UndoToolTip => _undo.Count == 0 ? "Nothing to undo" : $"Undo (Ctrl+Z) - {_undo.Count} change{(_undo.Count == 1 ? string.Empty : "s")}";

    /// <summary>The widget open in the set-up panel (#275), or null when it's closed.</summary>
    public DashboardWidgetViewModel? SetUpWidget
    {
        get => _setUpWidget;
        private set
        {
            var previous = _setUpWidget;
            if (!SetProperty(ref _setUpWidget, value))
            {
                return;
            }

            if (previous is not null)
            {
                previous.IsEditingWidget = false;
            }

            if (value is not null)
            {
                value.IsEditingWidget = true;
            }

            OnPropertyChanged(nameof(HasSetUpWidget));
            OnPropertyChanged(nameof(SetUpKind));
        }
    }

    public bool HasSetUpWidget => _setUpWidget is not null;

    /// <summary>"SET UP · GRAPH" - which kind of widget the panel is setting up.</summary>
    public string SetUpKind => _setUpWidget is null ? string.Empty
        : "SET UP · " + (WidgetNames.TryGetValue(_setUpWidget.WidgetType, out var name) ? name : _setUpWidget.WidgetType).ToUpperInvariant();

    private static readonly IReadOnlyDictionary<string, string> WidgetNames = WidgetPickerViewModel
        .DefaultCatalog(new WidgetPickerContext(Array.Empty<string>(), true, 0, true))
        .ToDictionary(e => e.WidgetType, e => e.Name);

    /// <summary>The widget the keys act on in edit mode (#278): arrows move it, Shift + arrows resize it, Del removes it.</summary>
    public DashboardWidgetViewModel? SelectedWidget
    {
        get => _selectedWidget;
        set
        {
            var previous = _selectedWidget;
            if (!SetProperty(ref _selectedWidget, value))
            {
                return;
            }

            if (previous is not null)
            {
                previous.IsSelected = false;
            }

            if (value is not null)
            {
                value.IsSelected = true;
            }
        }
    }

    /// <summary>The message after removing or duplicating a widget, with Undo (#276).</summary>
    public string? ToastText
    {
        get => _toastText;
        private set
        {
            if (SetProperty(ref _toastText, value))
            {
                OnPropertyChanged(nameof(IsToastVisible));
            }
        }
    }

    public bool IsToastVisible => _toastText is not null;

    /// <summary>The message offers Set up… - after Duplicate.</summary>
    public bool ToastCanSetUp => _toastWidgetId is not null;

    /// <summary>The "+ Add widget" space (#277): where the next widget fits, shown in edit mode.</summary>
    public bool ShowAddSpace => IsEditMode && Widgets.Count > 0;

    public double AddSpaceLeft => AddSpace().Column * DashboardWidget.CellSize;

    public double AddSpaceTop => AddSpace().Row * DashboardWidget.CellSize;

    public double AddSpaceWidth => DashboardWidget.DefaultColumnSpan * DashboardWidget.CellSize;

    public double AddSpaceHeight => DashboardWidget.DefaultRowSpan * DashboardWidget.CellSize;

    private (int Column, int Row) AddSpace()
        => DashboardGrid.FindFreeSpot(Widgets.Select(ToGridItem), DashboardWidget.DefaultColumnSpan, DashboardWidget.DefaultRowSpan);

    private static GridItem ToGridItem(DashboardWidgetViewModel w) => new(w.Id, w.Column, w.Row, w.ColumnSpan, w.RowSpan);

    // ------------------------------------------------------- the ⋯ menu (#274)

    /// <summary>Set up… (#275): the panel beside the dashboard, with this widget's options.</summary>
    public void SetUp(DashboardWidgetViewModel widget) => SetUpWidget = widget;

    /// <summary>Remove (#276): straight away, with Undo - no "are you sure?".</summary>
    public void Remove(DashboardWidgetViewModel widget)
    {
        if (ReferenceEquals(SetUpWidget, widget))
        {
            SetUpWidget = null;
        }

        if (ReferenceEquals(SelectedWidget, widget))
        {
            SelectedWidget = null;
        }

        var title = widget.Title;
        _layout.RemoveWidget(widget.Id);
        ShowToast($"Removed “{title}”", setUpWidgetId: null);
    }

    /// <summary>Duplicate (#279): a copy with its set-up, in the next free space.</summary>
    public void Duplicate(DashboardWidgetViewModel widget)
    {
        if (!widget.AllowsSeveral || _layout.DuplicateWidget(widget.Id) is not { } copy || !_widgetIndex.TryGetValue(copy.Id, out var added))
        {
            return;
        }

        added.Flash();
        WidgetAdded?.Invoke(this, added);
        ShowToast($"Duplicated “{widget.Title}”, with its set-up", setUpWidgetId: copy.Id);
    }

    /// <summary>Size ▸ Small, Medium, Large or Wide (#278): others move out of the way, as when resizing.</summary>
    public void Resize(DashboardWidgetViewModel widget, WidgetSize size)
    {
        (widget.ColumnSpan, widget.RowSpan) = DashboardGrid.SpanFor(size);
        Reflow(widget);
        CommitLayout();
    }

    /// <summary>Pushes whatever <paramref name="anchor"/> now overlaps down, cascading - see <see cref="DashboardGrid.PushDown"/>.</summary>
    public void Reflow(DashboardWidgetViewModel anchor)
    {
        var items = Widgets.Select(ToGridItem).ToList();
        DashboardGrid.PushDown(items.First(i => i.Id == anchor.Id), items);
        Apply(items);
    }

    /// <summary>The keys in edit mode (#278): arrows move the selected widget a cell, Shift + arrows resize it.</summary>
    public void Nudge(int columns, int rows, bool resize)
    {
        if (SelectedWidget is not { } widget)
        {
            return;
        }

        if (resize)
        {
            widget.ColumnSpan = Math.Max(DashboardWidget.MinColumnSpan, widget.ColumnSpan + columns);
            widget.RowSpan = Math.Max(DashboardWidget.MinRowSpan, widget.RowSpan + rows);
        }
        else
        {
            widget.Column = Math.Max(0, widget.Column + columns);
            widget.Row = Math.Max(0, widget.Row + rows);
        }

        Reflow(widget);
        CommitLayout();
    }

    private void TidyUp()
    {
        var items = Widgets.Select(ToGridItem).ToList();
        DashboardGrid.TidyUp(items);
        Apply(items);
        CommitLayout();
    }

    private void Apply(IEnumerable<GridItem> items)
    {
        foreach (var item in items)
        {
            if (_widgetIndex.TryGetValue(item.Id, out var widget))
            {
                widget.Column = item.Column;
                widget.Row = item.Row;
                widget.ColumnSpan = item.ColumnSpan;
                widget.RowSpan = item.RowSpan;
            }
        }
    }

    // ------------------------------------------------------------ Undo (#276)

    /// <summary>Every change to the layout is a step to go back to - recorded as it's saved.</summary>
    private void RecordChange()
    {
        if (_restoring)
        {
            return;
        }

        _undo.Add(_current);
        if (_undo.Count > MaxUndo)
        {
            _undo.RemoveAt(0);
        }

        _current = _layout.Snapshot();
        RaiseUndoChanged();
    }

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var target = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        RestoreTo(target);
        DismissToast();
        RaiseUndoChanged();
    }

    private void CancelEditing()
    {
        if (_editStart is { } start)
        {
            RestoreTo(start);
        }

        _undo.Clear();
        RaiseUndoChanged();
        SetUpWidget = null;
        DismissToast();
        IsEditMode = false;
    }

    /// <summary>
    /// Puts the layout back to <paramref name="target"/>. A widget whose set-up
    /// differs is made afresh, so it shows what it had (its sensors, its graph)
    /// rather than only moving back.
    /// </summary>
    private void RestoreTo(IReadOnlyList<DashboardWidget> target)
    {
        var before = _layout.Widgets.ToDictionary(w => w.Id, Serialise);
        foreach (var model in target)
        {
            if (before.TryGetValue(model.Id, out var json) && json != Serialise(model) && _widgetIndex.TryGetValue(model.Id, out var stale))
            {
                _widgetIndex.Remove(model.Id);
                Widgets.Remove(stale);
                (stale as IDisposable)?.Dispose();
            }
        }

        var setUpId = SetUpWidget?.Id;
        _restoring = true;
        try
        {
            _layout.Restore(target);
        }
        finally
        {
            _restoring = false;
        }

        _current = _layout.Snapshot();
        SetUpWidget = setUpId is not null && _widgetIndex.TryGetValue(setUpId, out var again) ? again : null;
    }

    private static string Serialise(DashboardWidget widget)
    {
        var copy = widget.Clone();
        copy.Column = copy.Row = copy.ColumnSpan = copy.RowSpan = 0;
        copy.Title = string.Empty;
        return System.Text.Json.JsonSerializer.Serialize(copy);
    }

    private void RaiseUndoChanged()
    {
        UndoCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(UndoToolTip));
    }

    private void ShowToast(string text, string? setUpWidgetId)
    {
        _toastWidgetId = setUpWidgetId;
        OnPropertyChanged(nameof(ToastCanSetUp));
        ToastText = text;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _toastTimer.Tick += (_, _) => DismissToast();
        _toastTimer.Start();
    }

    private void DismissToast()
    {
        _toastTimer?.Stop();
        _toastWidgetId = null;
        OnPropertyChanged(nameof(ToastCanSetUp));
        ToastText = null;
    }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Opens a sensor's device in LibreNMS. Shared by every Sensors widget, since it needs no per-widget state.</summary>
    public RelayCommand OpenDeviceCommand { get; }

    /// <summary>A short "45s" / "2:05" countdown to the next automatic refresh.</summary>
    public string NextRefreshText => PollAlignment.FormatRemaining(_sensorMonitor.SecondsUntilNextPoll());

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
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LastUpdatedText => _lastUpdated is null
        ? "never"
        : _lastUpdated.Value.LocalDateTime.ToString("HH:mm:ss");

    /// <summary>
    /// Called each time the tab is shown; starts the shared sensor monitor if
    /// nothing else has already (e.g. the Health tab), and asks it to poll
    /// right away so this tab is not left empty until the next scheduled tick.
    /// </summary>
    public void OnShown()
    {
        _sensorMonitor.Start();

        // Always started, never gated on _hasLoadedOnce - see the matching
        // comment in HealthViewModel: a poll triggered by another tab can mark
        // this one loaded before it is ever shown, which would otherwise skip
        // starting the countdown and leave it frozen between polls.
        _autoRefresh.Start();

        // Every visit, not gated on _hasLoadedOnce like the sensor refresh
        // below - a Graph widget has no shared background poll of its own
        // (see the class doc comment), so "come back to the Dashboard tab"
        // is the only signal it gets that it might be stale.
        ReloadGraphWidgets();

        if (_hasLoadedOnce)
        {
            return;
        }

        _sensorMonitor.RequestRefresh();
    }

    private void ReloadGraphWidgets()
    {
        foreach (var widget in Widgets.OfType<GraphWidgetViewModel>())
        {
            widget.Reload();
        }

        foreach (var widget in Widgets.OfType<WirelessWidgetViewModel>())
        {
            widget.Reload();
        }

        // The three share one fetch (IFleetPorts), so this is one request, not three.
        foreach (var widget in Widgets.OfType<TopWidgetViewModel>())
        {
            widget.Reload();
        }

        foreach (var widget in Widgets.OfType<LogFeedWidgetViewModel>())
        {
            widget.Reload();
        }
    }

    /// <summary>
    /// Persists every widget's current grid position/span in one shot. Called
    /// by the view once a drag or resize gesture ends - a single commit
    /// covers both the widget that moved and anything it displaced along the
    /// way, since live reflow already updated all of their view models.
    /// </summary>
    public void CommitLayout()
        => _layout.CommitLayout(Widgets.Select(w => (w.Id, w.Column, w.Row, w.ColumnSpan, w.RowSpan)));

    private void OnPollStarted(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() => IsBusy = true);

    private void OnPolled(object? sender, SensorPollResult result) => _dispatcher.InvokeAsync(() => ApplyPollResult(result));

    private void ApplyPollResult(SensorPollResult result)
    {
        IsBusy = false;
        OnPropertyChanged(nameof(NextRefreshText));

        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            StatusMessage = "Last refresh failed.";
            return;
        }

        ErrorMessage = null;

        if (Widgets.Count == 0)
        {
            _hasLoadedOnce = true;
            StatusMessage = "No widgets yet.";
            return;
        }

        // Alerts/AlertsGauge widgets need no data from here - they listen to
        // the app-wide AlertMonitor directly.
        var sensorWidgets = Widgets.OfType<SensorWidgetViewModel>().ToList();
        if (sensorWidgets.Count == 0)
        {
            _hasLoadedOnce = true;
            StatusMessage = "Up to date.";
            return;
        }

        // Only classes the app understands thresholds for - see SensorCategoryRegistry.
        var supported = result.Sensors.Where(s => SensorCategoryRegistry.Resolve(s.SensorClass) is not null).ToList();

        var connection = _session.Connection;
        var settings = _settings.Current;
        string DeviceNameFor(int deviceId) => _devices.Get(deviceId)?.BestName ?? $"device {deviceId}";

        foreach (var widget in sensorWidgets)
        {
            widget.ApplyFleet(supported, DeviceNameFor, connection, settings);
        }

        _lastFleet = supported;
        _lastDeviceNameFor = DeviceNameFor;
        _hasLoadedOnce = true;
        _lastUpdated = result.CompletedAt;

        var shown = sensorWidgets.Sum(w => w.Sensors.Count);
        StatusMessage = $"{shown} sensor(s) across {sensorWidgets.Count} widget(s).";
        OnPropertyChanged(nameof(LastUpdatedText));
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        OnPropertyChanged(nameof(ShowWelcome));

        foreach (var widget in Widgets.OfType<SensorWidgetViewModel>())
        {
            widget.ApplyThresholds(settings);
        }
    }

    // ---------------------------------------------------------------- widgets

    /// <summary>Signed in: make the widgets now (see <see cref="SyncWidgets"/>).</summary>
    private void OnSessionStateChanged(object? sender, EventArgs e)
        => _dispatcher.InvokeAsync(() =>
        {
            SyncWidgets();
            OnPropertyChanged(nameof(ShowWelcome));
        });

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        RecordChange();
        var previousIds = Widgets.Select(w => w.Id).ToHashSet();
        SyncWidgets();
        OnPropertyChanged(nameof(ShowAddSpace));
        OnPropertyChanged(nameof(AddSpaceLeft));
        OnPropertyChanged(nameof(AddSpaceTop));
        TidyUpCommand.RaiseCanExecuteChanged();

        // A brand new widget has nothing to show yet (SyncFrom only re-applies
        // a widget's own last-seen fleet). Seed it from the last real fetch
        // immediately - e.g. adding a widget or a sensor should feel instant,
        // not wait for the next poll - rather than hitting the API again for
        // what is purely a local layout/membership change.
        foreach (var widget in Widgets.OfType<SensorWidgetViewModel>())
        {
            if (!previousIds.Contains(widget.Id))
            {
                widget.ApplyFleet(_lastFleet, _lastDeviceNameFor, _session.Connection, _settings.Current);
            }
        }

        // One added from elsewhere (a port graph's Add to dashboard, #285)
        // stands out where it lands, as one added here does.
        if (previousIds.Count > 0)
        {
            foreach (var widget in Widgets.Where(w => !previousIds.Contains(w.Id) && !w.IsHighlighted))
            {
                widget.Flash();
            }
        }
    }

    /// <summary>Adds/removes/updates <see cref="Widgets"/> to match the persisted layout.</summary>
    private void SyncWidgets()
    {
        // Nothing to load until signed in (#231): the widgets are made once
        // there's a session, so none of them asks a server that isn't there.
        if (!_session.IsConnected)
        {
            return;
        }

        var models = _layout.Widgets;
        var incomingIds = models.Select(w => w.Id).ToHashSet();

        for (var i = Widgets.Count - 1; i >= 0; i--)
        {
            var widget = Widgets[i];
            if (!incomingIds.Contains(widget.Id))
            {
                _widgetIndex.Remove(widget.Id);
                Widgets.RemoveAt(i);

                // Unhooks a removed widget from the monitors it listens to.
                (widget as IDisposable)?.Dispose();
            }
        }

        foreach (var model in models)
        {
            if (_widgetIndex.TryGetValue(model.Id, out var existing))
            {
                existing.SyncFrom(model);
            }
            else
            {
                var created = CreateWidgetViewModel(model);
                created.Actions = this;
                _widgetIndex[model.Id] = created;
                Widgets.Add(created);
            }
        }
    }

    /// <summary>
    /// The widget picker (#204, #277): add the widgets chosen - several at
    /// once, in one step for Undo - each in the next free space, and flash
    /// them so they can be found. A single Sensors or Graph widget opens the
    /// set-up panel straight away, since it shows nothing until set up (#275).
    /// </summary>
    private void AddWidget()
    {
        var settings = _settings.Current;
        var context = new WidgetPickerContext(Widgets.Select(w => w.WidgetType).ToList(), settings.EnablePinnedDevices, settings.PinnedDevices.Count, _graylog.IsConfigured);
        var chosen = _windows.ShowWidgetPicker(WidgetPickerViewModel.DefaultCatalog(context));
        if (chosen.Count == 0)
        {
            return;
        }

        var placed = _layout.Widgets.Select(GridItem.From).ToList();
        var models = new List<DashboardWidget>();
        foreach (var entry in chosen)
        {
            var (column, row) = DashboardGrid.FindFreeSpot(placed, DashboardWidget.DefaultColumnSpan, DashboardWidget.DefaultRowSpan);
            var model = new DashboardWidget { WidgetType = entry.WidgetType, Title = entry.Name, Column = column, Row = row };
            models.Add(model);
            placed.Add(GridItem.From(model));
        }

        _layout.AddWidgets(models);

        DashboardWidgetViewModel? last = null;
        foreach (var model in models)
        {
            if (_widgetIndex.TryGetValue(model.Id, out var added))
            {
                added.Flash();
                last = added;
            }
        }

        if (last is not null)
        {
            WidgetAdded?.Invoke(this, last);
            if (models.Count == 1 && NeedsSetUp.Contains(last.WidgetType))
            {
                SetUp(last);
            }
        }
    }

    /// <summary>A Top widget row: a port opens its device on the Ports section with that port picked; a device opens Device Details.</summary>
    private void OpenTopRow(TopRowViewModel row)
    {
        if (row.PortId is { } portId)
        {
            _windows.ShowDevicePort(row.DeviceId, portId);
        }
        else
        {
            _windows.ShowDeviceDetail(row.DeviceId);
        }
    }

    private DashboardWidgetViewModel CreateWidgetViewModel(DashboardWidget model) => model.WidgetType switch
    {
        "Alerts" => new AlertsWidgetViewModel(_layout, model, _alertMonitor, _session, _settings, _devices, _windows),
        "AlertsGauge" => new AlertsGaugeWidgetViewModel(_layout, model, _alertMonitor, _windows),
        "DeviceStatus" => new DeviceStatusWidgetViewModel(_layout, model, _deviceMonitor, _windows),
        "RecentlyViewed" => new RecentlyViewedWidgetViewModel(_layout, model, _settings, _devices, _deviceMonitor, deviceId => _windows.ShowDeviceDetail(deviceId)),
        "PinnedDevices" => new PinnedDevicesWidgetViewModel(_layout, model, _settings, _devices, _deviceMonitor, deviceId => _windows.ShowDeviceDetail(deviceId)),
        "Graph" => new GraphWidgetViewModel(_layout, model, _deviceMonitor, _client, _logger, (deviceId, graphName) => _windows.ShowDeviceGraph(deviceId, graphName), (deviceId, ifName, graphType) => _windows.ShowDevicePortGraph(deviceId, ifName, graphType)),
        "Wireless" => new WirelessWidgetViewModel(_layout, model, _deviceMonitor, _client, _logger, deviceId => _windows.ShowDeviceWireless(deviceId)),
        DashboardWidgetTypes.TopInterfaces => new TopInterfacesWidgetViewModel(_layout, model, _fleetPorts, _devices, _settings, _deviceMonitor, _logger, OpenTopRow),
        DashboardWidgetTypes.TopErrors => new TopErrorsWidgetViewModel(_layout, model, _fleetPorts, _devices, _settings, _deviceMonitor, _logger, OpenTopRow),
        DashboardWidgetTypes.TopDevices => new TopDevicesWidgetViewModel(_layout, model, _fleetPorts, _devices, _settings, _deviceMonitor, _logger, OpenTopRow),
        DashboardWidgetTypes.EventLog => new EventLogWidgetViewModel(_layout, model, _client, _devices, _settings, _deviceMonitor, _windows, _logger),
        DashboardWidgetTypes.Graylog => new GraylogWidgetViewModel(_layout, model, _graylog, _devices, _settings, _deviceMonitor, _windows, _logger),
        "Sensors" => new SensorWidgetViewModel(_layout, model, OpenDeviceCommand),

        // A type from DashyNMS Mobile or a newer version: say so, rather than
        // passing it off as an empty Sensors widget (#196).
        _ => new UnsupportedWidgetViewModel(_layout, model),
    };

    public void Dispose()
    {
        _autoRefresh.Dispose();
        Welcome.Dispose();
        _settings.Changed -= OnSettingsChanged;
        _layout.Changed -= OnLayoutChanged;
        _session.StateChanged -= OnSessionStateChanged;
        _sensorMonitor.PollStarted -= OnPollStarted;
        _sensorMonitor.Polled -= OnPolled;

        foreach (var widget in Widgets)
        {
            (widget as IDisposable)?.Dispose();
        }
    }
}

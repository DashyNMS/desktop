using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// <see cref="IsEditMode"/>). Today the only widget type is "Sensors", each
/// showing whichever sensors were added to it specifically. One fetch here
/// covers every sensor widget, refreshed the same way as Health (initial load
/// on first view, then on the polling interval).
/// </summary>
public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly IWindowService _windows;
    private readonly IDashboardLayoutService _layout;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly Dictionary<string, DashboardWidgetViewModel> _widgetIndex = new();
    private readonly AutoRefreshTimer _autoRefresh;

    private CancellationTokenSource? _loadCts;
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
        ILibreNmsClient client,
        ISessionService session,
        ISettingsStore settings,
        IDeviceCache devices,
        IWindowService windows,
        IDashboardLayoutService layout,
        ILogger<DashboardViewModel> logger)
    {
        _client = client;
        _session = session;
        _settings = settings;
        _devices = devices;
        _windows = windows;
        _layout = layout;
        _logger = logger;

        Widgets = new ObservableCollection<DashboardWidgetViewModel>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _session.IsConnected && !IsBusy);
        OpenDeviceCommand = new RelayCommand(parameter =>
        {
            if (parameter is SensorItemViewModel { DeviceUrl: { } url })
            {
                _windows.OpenUrl(url);
            }
        });

        AddSensorWidgetCommand = new RelayCommand(() => _layout.AddWidget("Sensors", "Sensors"));

        _autoRefresh = new AutoRefreshTimer(() => _settings.Current.PollIntervalSeconds, () => _ = RefreshAsync());
        _autoRefresh.RemainingChanged += (_, _) => OnPropertyChanged(nameof(NextRefreshText));

        SyncWidgets();

        _settings.Changed += OnSettingsChanged;
        _layout.Changed += OnLayoutChanged;
    }

    /// <summary>The widgets on the canvas, in the order they were created.</summary>
    public ObservableCollection<DashboardWidgetViewModel> Widgets { get; }

    public RelayCommand AddSensorWidgetCommand { get; }

    /// <summary>True while the user is arranging the dashboard: widgets show drag/resize/remove handles.</summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        set => SetProperty(ref _isEditMode, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Opens a sensor's device in LibreNMS. Shared by every Sensors widget, since it needs no per-widget state.</summary>
    public RelayCommand OpenDeviceCommand { get; }

    /// <summary>A short "45s" / "2:05" countdown to the next automatic refresh.</summary>
    public string NextRefreshText => _autoRefresh.RemainingText;

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
    /// Called each time the tab is shown; loads once, then keeps refreshing
    /// automatically on the polling interval from Settings (same as Health).
    /// </summary>
    public void OnShown()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _ = RefreshAsync();
        _autoRefresh.Start();
    }

    private async Task RefreshAsync()
    {
        if (!_session.IsConnected)
        {
            StatusMessage = "Not connected.";
            return;
        }

        var sensorWidgets = Widgets.OfType<SensorWidgetViewModel>().ToList();
        if (sensorWidgets.Count == 0)
        {
            _hasLoadedOnce = true;
            StatusMessage = "No widgets yet.";
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var sensors = await _client.Sensors.ListAsync(token).ConfigureAwait(true);

            // Only classes the app understands thresholds for - see SensorCategoryRegistry.
            var supported = sensors.Where(s => SensorCategoryRegistry.Resolve(s.SensorClass) is not null).ToList();

            await _devices.EnsureCurrentAsync(supported.Select(s => s.DeviceId).Distinct(), token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

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
            _lastUpdated = DateTimeOffset.Now;

            var shown = sensorWidgets.Sum(w => w.Sensors.Count);
            StatusMessage = $"{shown} sensor(s) across {sensorWidgets.Count} widget(s).";
            OnPropertyChanged(nameof(LastUpdatedText));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load dashboard sensors");
            ErrorMessage = ex.ToUserMessage();
            StatusMessage = "Last refresh failed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load dashboard sensors");
            ErrorMessage = ex.Message;
            StatusMessage = "Last refresh failed.";
        }
        finally
        {
            IsBusy = false;
            _autoRefresh.Reset();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        foreach (var widget in Widgets.OfType<SensorWidgetViewModel>())
        {
            widget.ApplyThresholds(settings);
        }
    }

    // ---------------------------------------------------------------- widgets

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        var previousIds = Widgets.Select(w => w.Id).ToHashSet();
        SyncWidgets();

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
    }

    /// <summary>Adds/removes/updates <see cref="Widgets"/> to match the persisted layout.</summary>
    private void SyncWidgets()
    {
        var models = _layout.Widgets;
        var incomingIds = models.Select(w => w.Id).ToHashSet();

        for (var i = Widgets.Count - 1; i >= 0; i--)
        {
            var widget = Widgets[i];
            if (!incomingIds.Contains(widget.Id))
            {
                _widgetIndex.Remove(widget.Id);
                Widgets.RemoveAt(i);
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
                _widgetIndex[model.Id] = created;
                Widgets.Add(created);
            }
        }
    }

    private DashboardWidgetViewModel CreateWidgetViewModel(DashboardWidget model) => model.WidgetType switch
    {
        // "Sensors" is the only widget type today; fall back to it for any
        // future/unknown type so a layout from a newer version does not blow up.
        _ => new SensorWidgetViewModel(_layout, model, OpenDeviceCommand),
    };

    public void Dispose()
    {
        _autoRefresh.Dispose();
        _settings.Changed -= OnSettingsChanged;
        _layout.Changed -= OnLayoutChanged;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}

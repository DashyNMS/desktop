using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The "Wireless" dashboard widget (#55): one line per wireless controller -
/// its AP and client counts, coloured by its own LibreNMS limits (an AP
/// count dropping below its low limit goes red), down ones included - and
/// the fleet's client total. LibreNMS only lists wireless readings per
/// device, so which devices to ask is worked out once per session (see
/// <see cref="WirelessFleet"/>): one device of each OS is asked first, then
/// only the devices of the OSes that answered are polled - a handful of
/// calls per refresh rather than one per device. APs aren't totalled: a
/// clustered pair of controllers both report the same APs.
/// Refreshes with each device poll, on showing the Dashboard and on its
/// refresh button.
/// </summary>
public sealed class WirelessWidgetViewModel : DashboardWidgetViewModel, IDisposable
{
    private const int MaxConcurrentRequests = 4;

    private readonly DeviceMonitor _deviceMonitor;
    private readonly ILibreNmsClient _client;
    private readonly ILogger _logger;
    private readonly Action<int> _openDevice;
    private readonly Dispatcher _dispatcher;

    private readonly HashSet<string> _probedOses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _wirelessOses = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<Device> _fleet = Array.Empty<Device>();
    private bool _isLoading;
    private bool _hasLoaded;
    private string? _errorMessage;
    private int _loadVersion;

    /// <param name="openDevice">Opens a device's Device Details on its Wireless section.</param>
    public WirelessWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model, DeviceMonitor deviceMonitor, ILibreNmsClient client, ILogger logger, Action<int> openDevice)
        : base(layout, model)
    {
        _deviceMonitor = deviceMonitor;
        _client = client;
        _logger = logger;
        _openDevice = openDevice;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Controllers = new ObservableCollection<WirelessControllerItemViewModel>();
        OpenDeviceCommand = new RelayCommand(parameter =>
        {
            if (parameter is WirelessControllerItemViewModel item)
            {
                _openDevice(item.DeviceId);
            }
        });

        // The device list says which OSes there are - see the class remarks.
        // DeviceMonitor is lazily started, and this may be the only thing on
        // the Dashboard asking for it.
        _deviceMonitor.Polled += OnDevicesPolled;
        _deviceMonitor.Start();
        _deviceMonitor.RequestRefresh();
    }

    public ObservableCollection<WirelessControllerItemViewModel> Controllers { get; }

    public RelayCommand OpenDeviceCommand { get; }

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
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public bool HasControllers => Controllers.Count > 0;

    /// <summary>Only before the first result - later refreshes update in place.</summary>
    public bool ShowLoading => IsLoading && !_hasLoaded;

    public bool ShowEmptyMessage => _hasLoaded && !IsLoading && !HasControllers && !HasError;

    public string TotalClientsText => Controllers.Where(c => c.Clients is not null).Sum(c => c.Clients!.Value).ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>"3 controllers", "3 controllers, 1 down".</summary>
    public string ControllersText
    {
        get
        {
            var down = Controllers.Count(c => c.IsDown);
            var text = Controllers.Count.ToString(CultureInfo.CurrentCulture) + (Controllers.Count == 1 ? " controller" : " controllers");
            return down > 0 ? $"{text}, {down} down" : text;
        }
    }

    /// <summary>Re-reads every controller's counts - the Dashboard calls this when it's shown and on its refresh button.</summary>
    public void Reload()
    {
        if (_fleet.Count > 0)
        {
            _ = LoadAsync();
        }
    }

    private void OnDevicesPolled(object? sender, DevicePollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            _fleet = result.Devices;
            _ = LoadAsync();
        });
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true;

        try
        {
            // Learn about any OS not asked about yet - all of them the first
            // time, then only ones that newly appear in the fleet.
            var probes = WirelessFleet.ProbeCandidates(_fleet, _probedOses);
            if (probes.Count > 0)
            {
                var answers = await FetchAsync(probes).ConfigureAwait(true);
                // One that couldn't be asked is asked again next time.
                foreach (var probe in probes.Where(p => answers.ContainsKey(p.DeviceId)))
                {
                    _probedOses.Add(probe.Os!);
                    if (answers[probe.DeviceId].Any(s => !s.Deleted))
                    {
                        _wirelessOses.Add(probe.Os!);
                    }
                }
            }

            var devices = WirelessFleet.DevicesToPoll(_fleet, _wirelessOses);
            var readings = await FetchAsync(devices).ConfigureAwait(true);

            if (version != _loadVersion)
            {
                return;
            }

            var failed = devices.Count(d => !readings.ContainsKey(d.DeviceId));
            ErrorMessage = failed > 0 && failed == devices.Count ? "Couldn't reach LibreNMS for wireless data." : null;

            Apply(devices
                .Where(d => readings.ContainsKey(d.DeviceId))
                .Select(d => (Device: d, Summary: WirelessFleet.Summarise(d.DeviceId, readings[d.DeviceId])))
                .Where(x => x.Summary.HasAny)
                .ToList());
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

    /// <summary>Each device's wireless readings, a few requests at a time; a device that fails is left out rather than failing the lot.</summary>
    private async Task<Dictionary<int, IReadOnlyList<WirelessSensor>>> FetchAsync(IReadOnlyList<Device> devices)
    {
        var results = new Dictionary<int, IReadOnlyList<WirelessSensor>>();
        using var gate = new System.Threading.SemaphoreSlim(MaxConcurrentRequests);

        async Task One(Device device)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sensors = await _client.Devices.GetWirelessSensorsAsync(device.DeviceId).ConfigureAwait(false);
                lock (results)
                {
                    results[device.DeviceId] = sensors;
                }
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogDebug(ex, "Could not load wireless sensors for device {DeviceId}", device.DeviceId);
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(devices.Select(One)).ConfigureAwait(true);
        return results;
    }

    private void Apply(IReadOnlyList<(Device Device, WirelessControllerSummary Summary)> rows)
    {
        // Down first, then worst state, then by name - what needs a look
        // comes to the top.
        var ordered = rows
            .Select(r => new WirelessControllerItemViewModel(r.Device, r.Summary))
            .OrderByDescending(c => c.IsDown)
            .ThenByDescending(c => c.Severity)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Controllers.Clear();
        foreach (var item in ordered)
        {
            Controllers.Add(item);
        }

        OnPropertyChanged(nameof(TotalClientsText));
        OnPropertyChanged(nameof(ControllersText));
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasControllers));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowEmptyMessage));
    }

    public void Dispose() => _deviceMonitor.Polled -= OnDevicesPolled;
}

/// <summary>One controller's line on the Wireless widget.</summary>
public sealed class WirelessControllerItemViewModel
{
    public WirelessControllerItemViewModel(Device device, WirelessControllerSummary summary)
    {
        DeviceId = device.DeviceId;
        Name = device.BestName;
        IsDown = !device.Status;
        ApCount = summary.ApCount;
        Clients = summary.Clients;
        Severity = IsDown ? AlertSeverity.Critical : summary.Severity;
    }

    public int DeviceId { get; }

    public string Name { get; }

    public bool IsDown { get; }

    public double? ApCount { get; }

    public double? Clients { get; }

    /// <summary>Critical while the controller is down; otherwise the worst of its readings against their LibreNMS limits.</summary>
    public AlertSeverity Severity { get; }

    public string ApText => WirelessSensorClasses.Format(WirelessSensorClasses.ApCount, ApCount);

    public string ClientsText => WirelessSensorClasses.Format(WirelessSensorClasses.Clients, Clients);

    /// <summary>A down controller's counts are its last before it went down.</summary>
    public string ToolTip => IsDown ? $"{Name} is down - these are its last counts." : Name;
}

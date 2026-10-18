using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
/// The Intelligence tab's context sidebar - what the conversation is about:
/// its Spotlight (devices, a group or a location the agent is kept to), the
/// devices it has mentioned with their state, their open alerts, and what
/// was looked up. Everything here comes from DashyNMS's own live data, not
/// from the model.
/// </summary>
public sealed partial class IntelligenceContextViewModel : ObservableObject
{
    /// <summary>The most devices the Mentioned list keeps - the newest first.</summary>
    private const int MaxMentioned = 12;

    private readonly IDeviceCache _devices;
    private readonly ILibreNmsClient _client;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;

    private IReadOnlyList<Alert> _alerts = Array.Empty<Alert>();
    private SpotlightKind _pickerKind = SpotlightKind.Device;
    private SpotlightOption? _pickerSelection;
    private bool _pickerOpen;
    private bool _loadedScopes;
    private List<SpotlightOption> _groups = new();
    private List<SpotlightOption> _locations = new();

    public IntelligenceContextViewModel(
        IDeviceCache devices,
        ILibreNmsClient client,
        ISettingsStore settings,
        IWindowService windows,
        AlertMonitor alerts,
        ILogger logger)
    {
        _devices = devices;
        _client = client;
        _settings = settings;
        _windows = windows;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        alerts.Polled += (_, result) =>
        {
            if (result.Succeeded)
            {
                _dispatcher.InvokeAsync(() =>
                {
                    _alerts = result.Alerts;
                    RefreshMentioned();
                });
            }
        };

        OpenDeviceCommand = new RelayCommand(p =>
        {
            if (p is MentionedDeviceViewModel device)
            {
                _windows.ShowDeviceDetail(device.DeviceId);
            }
            else if (p is ContextAlertViewModel alert)
            {
                _windows.ShowDeviceDetail(alert.DeviceId);
            }
        });
        SpotlightDeviceCommand = new RelayCommand(p =>
        {
            if (p is MentionedDeviceViewModel device)
            {
                AddSpotlight(new SpotlightOption(SpotlightKind.Device, device.DeviceId, device.Name));
            }
        });
        RemoveSpotlightCommand = new RelayCommand(p =>
        {
            if (p is SpotlightOption option)
            {
                Spotlight.Remove(option);
                RaiseSpotlight();
            }
        });
        ClearSpotlightCommand = new RelayCommand(() =>
        {
            Spotlight.Clear();
            RaiseSpotlight();
        }, () => Spotlight.Count > 0);
        OpenPickerCommand = new AsyncRelayCommand(OpenPickerAsync);
        ClosePickerCommand = new RelayCommand(() => PickerOpen = false);
        AddPickedCommand = new RelayCommand(() =>
        {
            if (_pickerSelection is { } picked)
            {
                AddSpotlight(picked);
                PickerSelection = null;
                PickerOpen = false;
            }
        }, () => _pickerSelection is not null);
        PickDevicesCommand = new RelayCommand(() => PickerKind = SpotlightKind.Device);
        PickGroupCommand = new RelayCommand(() => PickerKind = SpotlightKind.Group);
        PickLocationCommand = new RelayCommand(() => PickerKind = SpotlightKind.Location);
    }

    public ObservableCollection<MentionedDeviceViewModel> Mentioned { get; } = new();

    public ObservableCollection<ContextAlertViewModel> Alerts { get; } = new();

    public ObservableCollection<string> LookedAt { get; } = new();

    /// <summary>What the agent is kept to: devices, or one group or location.</summary>
    public ObservableCollection<SpotlightOption> Spotlight { get; } = new();

    public ObservableCollection<SpotlightOption> PickerOptions { get; } = new();

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand SpotlightDeviceCommand { get; }

    public RelayCommand RemoveSpotlightCommand { get; }

    public RelayCommand ClearSpotlightCommand { get; }

    public AsyncRelayCommand OpenPickerCommand { get; }

    public RelayCommand ClosePickerCommand { get; }

    public RelayCommand AddPickedCommand { get; }

    public RelayCommand PickDevicesCommand { get; }

    public RelayCommand PickGroupCommand { get; }

    public RelayCommand PickLocationCommand { get; }

    public bool HasMentioned => Mentioned.Count > 0;

    public bool HasAlerts => Alerts.Count > 0;

    public bool HasLookedAt => LookedAt.Count > 0;

    public bool HasSpotlight => Spotlight.Count > 0;

    public string AlertsHeader => Alerts.Count == 0 ? "ALERTS" : $"ALERTS · {Alerts.Count}";

    public string SpotlightSummary => Spotlight.Count == 0 ? "Whole network" : string.Join(", ", Spotlight.Select(s => s.Name));

    /// <summary>The scope as the agent is told it - null for the whole network.</summary>
    public string? SpotlightForAgent
    {
        get
        {
            if (Spotlight.Count == 0)
            {
                return null;
            }

            var devices = Spotlight.Where(s => s.Kind == SpotlightKind.Device).Select(s => s.Name).ToList();
            var parts = new List<string>();
            if (devices.Count > 0)
            {
                parts.Add((devices.Count == 1 ? "the device " : "the devices ") + string.Join(", ", devices));
            }

            parts.AddRange(Spotlight.Where(s => s.Kind == SpotlightKind.Group).Select(s => "the device group \"" + s.Name + "\""));
            parts.AddRange(Spotlight.Where(s => s.Kind == SpotlightKind.Location).Select(s => "the location \"" + s.Name + "\""));
            return string.Join(" and ", parts);
        }
    }

    public bool PickerOpen
    {
        get => _pickerOpen;
        set => SetProperty(ref _pickerOpen, value);
    }

    public SpotlightKind PickerKind
    {
        get => _pickerKind;
        set
        {
            if (SetProperty(ref _pickerKind, value))
            {
                OnPropertyChanged(nameof(IsPickingDevices));
                OnPropertyChanged(nameof(IsPickingGroup));
                OnPropertyChanged(nameof(IsPickingLocation));
                FillPicker();
            }
        }
    }

    public bool IsPickingDevices => _pickerKind == SpotlightKind.Device;

    public bool IsPickingGroup => _pickerKind == SpotlightKind.Group;

    public bool IsPickingLocation => _pickerKind == SpotlightKind.Location;

    public SpotlightOption? PickerSelection
    {
        get => _pickerSelection;
        set
        {
            if (SetProperty(ref _pickerSelection, value))
            {
                AddPickedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The model ran a tool: note the devices its arguments name, and what it looked at.</summary>
    public void OnToolStarted(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is "device" or "devices" or "host" or "hostname")
                {
                    foreach (var value in property.Value.ValueKind == JsonValueKind.Array ? property.Value.EnumerateArray().ToList() : new List<JsonElement> { property.Value })
                    {
                        if (Resolve(value.ToString()) is { } device)
                        {
                            Mention(device);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    public void OnLookedAt(string subject)
    {
        if (!LookedAt.Contains(subject))
        {
            LookedAt.Add(subject);
            OnPropertyChanged(nameof(HasLookedAt));
        }
    }

    /// <summary>An answer finished: the devices it names by hostname, name or address.</summary>
    public void OnAnswer(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !_devices.IsLoaded)
        {
            return;
        }

        var index = NameIndex();
        var seen = new HashSet<int>();
        foreach (Match m in Word().Matches(text))
        {
            var token = m.Value.Trim('.', '-', '_').ToLowerInvariant();
            if (index.TryGetValue(token, out var device) && seen.Add(device.DeviceId))
            {
                Mention(device);
            }
        }
    }

    /// <summary>A new conversation: what it's about starts again, the Spotlight stays.</summary>
    public void Clear()
    {
        Mentioned.Clear();
        Alerts.Clear();
        LookedAt.Clear();
        RaiseLists();
    }

    private void Mention(Device device)
    {
        var existing = Mentioned.FirstOrDefault(m => m.DeviceId == device.DeviceId);
        if (existing is not null)
        {
            Mentioned.Move(Mentioned.IndexOf(existing), 0);
            return;
        }

        Mentioned.Insert(0, MentionedDeviceViewModel.From(device, _settings.Current.DeviceNameStyle));
        while (Mentioned.Count > MaxMentioned)
        {
            Mentioned.RemoveAt(Mentioned.Count - 1);
        }

        RefreshMentioned();
    }

    /// <summary>Each mentioned device's current state, and the open alerts on them.</summary>
    private void RefreshMentioned()
    {
        foreach (var mentioned in Mentioned)
        {
            if (_devices.Get(mentioned.DeviceId) is { } device)
            {
                mentioned.Update(device, _alerts.Count(a => a.DeviceId == device.DeviceId && a.State != AlertState.Recovered));
            }
        }

        var ids = Mentioned.Select(m => m.DeviceId).ToHashSet();
        var open = _alerts
            .Where(a => ids.Contains(a.DeviceId) && a.State != AlertState.Recovered)
            .OrderBy(a => a.IsAcknowledged)
            .ThenBy(a => a.Severity == AlertSeverity.Critical ? 0 : 1)
            .ThenByDescending(a => a.Timestamp)
            .Take(8)
            .Select(a => new ContextAlertViewModel(a, Mentioned.FirstOrDefault(m => m.DeviceId == a.DeviceId)?.Name ?? $"Device {a.DeviceId}"))
            .ToList();

        Alerts.Clear();
        foreach (var alert in open)
        {
            Alerts.Add(alert);
        }

        RaiseLists();
    }

    private Device? Resolve(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && _devices.Get(id) is { } byId)
        {
            return byId;
        }

        return _devices.FindByAddress(trimmed)
               ?? (NameIndex().TryGetValue(trimmed.ToLowerInvariant(), out var byName) ? byName : null);
    }

    /// <summary>Every device by each of its names, lower-cased: hostname, sysName, display name and IP.</summary>
    private Dictionary<string, Device> NameIndex()
    {
        var index = new Dictionary<string, Device>(StringComparer.Ordinal);
        foreach (var device in _devices.All)
        {
            foreach (var name in new[] { device.Hostname, device.SysName, device.BestName, device.Ip })
            {
                if (!string.IsNullOrWhiteSpace(name) && name.Length >= 3)
                {
                    index.TryAdd(name.Trim().ToLowerInvariant(), device);
                }
            }
        }

        return index;
    }

    private void AddSpotlight(SpotlightOption option)
    {
        // A group or location replaces anything else; devices add up.
        if (option.Kind != SpotlightKind.Device || Spotlight.Any(s => s.Kind != SpotlightKind.Device))
        {
            Spotlight.Clear();
        }

        if (!Spotlight.Contains(option))
        {
            Spotlight.Add(option);
        }

        RaiseSpotlight();
    }

    private async Task OpenPickerAsync()
    {
        PickerOpen = true;
        if (!_loadedScopes)
        {
            _loadedScopes = true;
            try
            {
                var groups = await _client.DeviceGroups.ListAsync(CancellationToken.None).ConfigureAwait(true);
                _groups = groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => new SpotlightOption(SpotlightKind.Group, g.Id, g.Name)).ToList();
                var locations = await _client.Locations.ListAsync(CancellationToken.None).ConfigureAwait(true);
                _locations = locations.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).Select(l => new SpotlightOption(SpotlightKind.Location, l.Id, l.Name)).ToList();
            }
            catch (Exception ex) when (ex is LibreNmsApiException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "Intelligence: couldn't list groups and locations for Spotlight");
                _loadedScopes = false;
            }
        }

        FillPicker();
    }

    private void FillPicker()
    {
        var style = _settings.Current.DeviceNameStyle;
        var options = _pickerKind switch
        {
            SpotlightKind.Group => _groups,
            SpotlightKind.Location => _locations,
            _ => _devices.All
                .Select(d => new SpotlightOption(SpotlightKind.Device, d.DeviceId, style.Resolve(d, d.Hostname)))
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        PickerOptions.Clear();
        foreach (var option in options)
        {
            PickerOptions.Add(option);
        }

        PickerSelection = null;
    }

    private void RaiseLists()
    {
        OnPropertyChanged(nameof(HasMentioned));
        OnPropertyChanged(nameof(HasAlerts));
        OnPropertyChanged(nameof(HasLookedAt));
        OnPropertyChanged(nameof(AlertsHeader));
    }

    private void RaiseSpotlight()
    {
        OnPropertyChanged(nameof(HasSpotlight));
        OnPropertyChanged(nameof(SpotlightSummary));
        OnPropertyChanged(nameof(SpotlightForAgent));
        ClearSpotlightCommand.RaiseCanExecuteChanged();
    }

    // Hostnames, sysNames and IPv4 addresses as they appear in an answer.
    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9._-]{2,}")]
    private static partial Regex Word();
}

public enum SpotlightKind
{
    Device,
    Group,
    Location,
}

/// <summary>One thing the conversation is narrowed to.</summary>
public sealed record SpotlightOption(SpotlightKind Kind, int Id, string Name)
{
    public string KindText => Kind switch
    {
        SpotlightKind.Group => "Group",
        SpotlightKind.Location => "Location",
        _ => "Device",
    };

    public override string ToString() => Name;
}

/// <summary>A device the conversation has mentioned, with its live state.</summary>
public sealed class MentionedDeviceViewModel : ObservableObject
{
    private string _stateText = string.Empty;
    private string _severity = "ok";
    private int _openAlerts;

    private MentionedDeviceViewModel(int deviceId, string name, string detail)
    {
        DeviceId = deviceId;
        Name = name;
        Detail = detail;
    }

    public static MentionedDeviceViewModel From(Device device, DeviceNameStyle style)
    {
        var model = new MentionedDeviceViewModel(
            device.DeviceId,
            style.Resolve(device, device.Hostname),
            string.Join(" · ", new[] { device.Ip, device.Hardware }.Where(s => !string.IsNullOrWhiteSpace(s))));
        model.Update(device, 0);
        return model;
    }

    public int DeviceId { get; }

    public string Name { get; }

    public string Detail { get; }

    /// <summary>" · 192.0.2.10 · C9500-24Y4C", or nothing when there's neither.</summary>
    public string DetailSuffix => string.IsNullOrEmpty(Detail) ? string.Empty : " · " + Detail;

    /// <summary>"ok", "down" or "other" - drives the status dot.</summary>
    public string Severity
    {
        get => _severity;
        private set => SetProperty(ref _severity, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public int OpenAlerts
    {
        get => _openAlerts;
        private set
        {
            if (SetProperty(ref _openAlerts, value))
            {
                OnPropertyChanged(nameof(HasOpenAlerts));
            }
        }
    }

    public bool HasOpenAlerts => _openAlerts > 0;

    public void Update(Device device, int openAlerts)
    {
        var disabled = device.Disabled || device.Ignore;
        Severity = disabled ? "other" : device.Status ? "ok" : "down";
        StateText = disabled ? "Disabled" : device.Status ? "Up" : "Down";
        OpenAlerts = openAlerts;
    }
}

/// <summary>An open alert on a mentioned device.</summary>
public sealed class ContextAlertViewModel
{
    public ContextAlertViewModel(Alert alert, string deviceName)
    {
        DeviceId = alert.DeviceId;
        Rule = alert.DisplayRuleName;
        DeviceName = deviceName;
        Severity = alert.IsAcknowledged ? "other" : alert.Severity == AlertSeverity.Critical ? "down" : "warning";
        Age = alert.Timestamp is { } at ? Ago(DateTime.Now - at) : string.Empty;
    }

    public int DeviceId { get; }

    public string Rule { get; }

    public string DeviceName { get; }

    /// <summary>"down" (critical), "warning", or "other" (acknowledged) - drives the dot.</summary>
    public string Severity { get; }

    public string Age { get; }

    private static string Ago(TimeSpan span) => span.TotalMinutes < 1 ? "now"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours}h"
        : $"{(int)span.TotalDays}d";
}

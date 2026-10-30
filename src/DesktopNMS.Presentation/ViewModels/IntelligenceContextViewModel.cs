using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Spotlights;
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
    private readonly IUiDispatcher _dispatcher;

    private IReadOnlyList<Alert> _alerts = Array.Empty<Alert>();
    private bool _isScopeLocked;
    private DateTimeOffset? _lastAlertRefresh;
    private bool _alertRefreshFailed;
    private int _matchingAlertCount;

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
        _dispatcher = UiThread.Dispatcher;

        alerts.Polled += (_, result) =>
        {
            if (result.Succeeded)
            {
                _dispatcher.InvokeAsync(() =>
                {
                    _alerts = result.Alerts;
                    _lastAlertRefresh = result.CompletedAt;
                    _alertRefreshFailed = false;
                    RefreshMentioned();
                });
            }
            else _dispatcher.InvokeAsync(() => { _alertRefreshFailed = true; OnPropertyChanged(nameof(AlertFreshness)); });
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
        }, _ => CanEditSpotlight);
        RemoveSpotlightCommand = new RelayCommand(p =>
        {
            if (!CanEditSpotlight) return;
            if (p is SpotlightOption option)
            {
                Spotlight.Remove(option);
                RaiseSpotlight();
            }
        }, _ => CanEditSpotlight);
        ClearSpotlightCommand = new RelayCommand(() =>
        {
            if (!CanEditSpotlight) return;
            Spotlight.Clear();
            RaiseSpotlight();
        }, () => CanEditSpotlight && Spotlight.Count > 0);
        Picker = new SpotlightPickerViewModel(devices, client, settings, logger, option => AddSpotlight(option), () => CanEditSpotlight);
    }

    public ObservableCollection<MentionedDeviceViewModel> Mentioned { get; } = new();

    public ObservableCollection<ContextAlertViewModel> Alerts { get; } = new();

    public ObservableCollection<string> LookedAt { get; } = new();

    /// <summary>What the agent is kept to: devices, or one group or location.</summary>
    public ObservableCollection<SpotlightOption> Spotlight { get; } = new();

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand SpotlightDeviceCommand { get; }

    public RelayCommand RemoveSpotlightCommand { get; }

    public RelayCommand ClearSpotlightCommand { get; }

    /// <summary>Choosing what to spotlight - the same picker as the Dashboard's log cards (#552).</summary>
    public SpotlightPickerViewModel Picker { get; }

    public bool HasMentioned => Mentioned.Count > 0;

    public bool HasAlerts => Alerts.Count > 0;

    public bool HasOpenNetworkAlerts => _alerts.Any(alert => alert.State != AlertState.Recovered);

    public bool HasLookedAt => LookedAt.Count > 0;

    public bool HasSpotlight => Spotlight.Count > 0;

    public string AlertsHeader => "OPEN ALERTS ON MENTIONED DEVICES";
    public string AlertCountLabel => _matchingAlertCount > Alerts.Count ? $"Showing {Alerts.Count} of {_matchingAlertCount}" : $"{Alerts.Count} open alerts";
    public string DeviceFreshness => _devices.LastRefreshed is { } at ? $"Cached device data · refreshed {at.ToLocalTime():g}" : "Cached device data · refresh time unavailable";
    public string AlertFreshness => (_alertRefreshFailed ? "Refresh failed · " : "") + (_lastAlertRefresh is { } at ? $"last successful alert poll {at.ToLocalTime():g}" : "No successful alert poll yet");
    public bool CanEditSpotlight => !_isScopeLocked;
    public bool IsScopeLocked
    {
        get => _isScopeLocked;
        set
        {
            if (!SetProperty(ref _isScopeLocked, value)) return;
            OnPropertyChanged(nameof(CanEditSpotlight));
            Picker.RefreshCanEdit();
            SpotlightDeviceCommand.RaiseCanExecuteChanged();
            RemoveSpotlightCommand.RaiseCanExecuteChanged();
            ClearSpotlightCommand.RaiseCanExecuteChanged();
        }
    }

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

            return string.Join("; ", Spotlight.Select(s => s.AgentDescription));
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

    public void RestoreChatContext(IEnumerable<SavedChatSpotlight> spotlight, IEnumerable<string> lookedAt, IEnumerable<string> answers)
    {
        Clear();
        Spotlight.Clear();
        foreach (var item in spotlight)
        {
            if (Enum.IsDefined(typeof(SpotlightKind), item.Kind))
                Spotlight.Add(new SpotlightOption((SpotlightKind)item.Kind, item.Id, item.Name) { Object = item.Object });
        }
        RaiseSpotlight();
        foreach (var item in lookedAt) OnLookedAt(item);
        foreach (var answer in answers) OnAnswer(answer);
    }

    /// <summary>A new conversation: what it's about starts again, the Spotlight stays.</summary>
    public void Clear()
    {
        Mentioned.Clear();
        Alerts.Clear();
        LookedAt.Clear();
        _matchingAlertCount = 0;
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

    /// <summary>Each mentioned device's current state, and the open alerts on them - one per device and rule, however many faults (#354).</summary>
    private void RefreshMentioned()
    {
        var ids = Mentioned.Select(m => m.DeviceId).ToHashSet();
        var groups = AlertGrouping.ByDeviceAndRule(_alerts.Where(a => ids.Contains(a.DeviceId) && a.State != AlertState.Recovered));
        foreach (var mentioned in Mentioned)
        {
            if (_devices.Get(mentioned.DeviceId) is { } device)
            {
                mentioned.Update(device, groups.Count(g => g.DeviceId == device.DeviceId));
            }
        }

        _matchingAlertCount = groups.Count;
        var open = groups
            .OrderBy(g => g.State == AlertState.Acknowledged)
            .ThenBy(g => g.Severity == AlertSeverity.Critical ? 0 : 1)
            .ThenByDescending(g => g.LastChanged)
            .Take(8)
            .Select(g => new ContextAlertViewModel(g, Mentioned.FirstOrDefault(m => m.DeviceId == g.DeviceId)?.Name ?? $"Device {g.DeviceId}"))
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

    public bool AddSpotlight(SpotlightOption option, bool replace = false)
    {
        if (!CanEditSpotlight || option.Id <= 0 || (option.Object is { IsValid: false })) return false;
        if (replace) Spotlight.Clear();
        if (!Spotlight.Any(s => s.Kind == option.Kind && s.Id == option.Id))
        {
            if (Spotlight.Count >= 32) return false;
            Spotlight.Add(option);
        }
        RaiseSpotlight();
        return true;
    }

    private void RaiseLists()
    {
        OnPropertyChanged(nameof(HasMentioned));
        OnPropertyChanged(nameof(HasAlerts));
        OnPropertyChanged(nameof(HasLookedAt));
        OnPropertyChanged(nameof(AlertsHeader));
        OnPropertyChanged(nameof(AlertCountLabel));
        OnPropertyChanged(nameof(DeviceFreshness));
        OnPropertyChanged(nameof(AlertFreshness));
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

/// <summary>One thing the conversation is narrowed to.</summary>
public sealed record SpotlightOption(SpotlightKind Kind, int Id, string Name)
{
    public IntelligenceObjectReference? Object { get; init; }
    public IntelligenceObjectReference Identity => Object ?? new(Kind.ToString().ToLowerInvariant(), Id);
    public string AgentDescription => $"{KindText} \"{Name}\" ({Identity.Kind}_id={Id}"
        + (Identity.DeviceId is { } device ? $", device_id={device}" : "")
        + (Identity.RuleId is { } rule ? $", rule_id={rule}" : "") + ")";
    public string KindText => Kind switch
    {
        SpotlightKind.Group => "Group",
        SpotlightKind.Location => "Location",
        SpotlightKind.Sensor => "Sensor",
        SpotlightKind.Alert => "Alert",
        SpotlightKind.Fault => "Fault",
        SpotlightKind.Port => "Interface",
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
    public ContextAlertViewModel(AlertGroup group, string deviceName)
    {
        DeviceId = group.DeviceId;
        Rule = group.FaultCount > 1 ? $"{group.DisplayRuleName} · {group.FaultCount} faults" : group.DisplayRuleName;
        DeviceName = deviceName;
        Severity = group.Severity switch { AlertSeverity.Critical => "down", AlertSeverity.Warning => "warning", AlertSeverity.Ok => "ok", _ => "other" };
        StateLabel = group.Severity.ToDisplayString()
            + (group.IsPartlyAcknowledged ? " · Partly acknowledged" : group.State == AlertState.Acknowledged ? " · Acknowledged" : " · Active");
        Age = group.LastChanged is { } at ? Ago(DateTime.Now - at) : string.Empty;
    }

    public int DeviceId { get; }

    public string Rule { get; }

    public string DeviceName { get; }

    /// <summary>Severity remains independent of acknowledgement.</summary>
    public string Severity { get; }
    public string StateLabel { get; }

    public string Age { get; }

    private static string Ago(TimeSpan span) => span.TotalMinutes < 1 ? "now"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours}h"
        : $"{(int)span.TotalDays}d";
}

using System;
using System.Collections.Generic;
using System.Linq;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One thing a device is connected to, on its Neighbours tab and the
/// Overview's Connected to card: the neighbour (the LibreNMS device when it
/// is one), both ends of the cable, and how it was found. Clicking a known
/// device moves the window to it (#58).
/// </summary>
public sealed class DeviceNeighbourItemViewModel : ObservableObject
{
    private readonly DeviceNeighbour _neighbour;
    private readonly Device? _device;
    private string? _remotePortName;

    public DeviceNeighbourItemViewModel(DeviceNeighbour neighbour, Device? device, string? deviceName, PortItemViewModel? localPort, string thisDeviceName, Action<int> openDevice)
    {
        _neighbour = neighbour;
        _device = device;
        _remotePortName = neighbour.RemotePortName;
        LocalPort = localPort;
        ThisDeviceName = thisDeviceName;
        Name = deviceName ?? neighbour.RemoteName;

        OpenCommand = new RelayCommand(() => openDevice(neighbour.RemoteDeviceId!.Value), () => IsKnownDevice);
    }

    public DeviceNeighbour Model => _neighbour;

    public RelayCommand OpenCommand { get; }

    /// <summary>The neighbour: its LibreNMS name when it's a device LibreNMS monitors, else the name it announced.</summary>
    public string Name { get; }

    private string ThisDeviceName { get; }

    /// <summary>A device LibreNMS monitors - clickable, with its state.</summary>
    public bool IsKnownDevice => _neighbour.RemoteDeviceId is > 0 && _device is not null;

    /// <summary>For the state dot: the device's own state, or Unknown for one LibreNMS doesn't monitor.</summary>
    public DeviceState? State => _device?.State;

    public AlertSeverity StateSeverity => _device?.State switch
    {
        DeviceState.Up => AlertSeverity.Ok,
        DeviceState.Down => AlertSeverity.Critical,
        DeviceState.Maintenance => AlertSeverity.Warning,
        _ => AlertSeverity.Unknown,
    };

    public string StateText => _device is null ? "Not monitored" : _device.State.ToDisplayString();

    /// <summary>Under the name: "C9300-48P · Network · 192.0.2.10", or what it announced for one LibreNMS doesn't monitor.</summary>
    public string Description
    {
        get
        {
            var parts = _device is null
                ? new[] { _neighbour.Platform }
                : new[] { _device.Hardware, TitleCase(_device.Type), _device.Ip };
            var text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            return text.Length > 0 ? text : "No details announced";
        }
    }

    /// <summary>This device's end of the cable.</summary>
    public string LocalPortText => LocalPort?.DisplayName ?? _neighbour.LocalPortName ?? "Unknown port";

    /// <summary>The neighbour's end of the cable.</summary>
    public string RemotePortText => !string.IsNullOrWhiteSpace(_remotePortName) ? PortLabels.FromNeighbourPort(_remotePortName) ?? _remotePortName! : "Unknown port";

    /// <summary>"Gi1/0/24 ⇄ Te1/1/1".</summary>
    public string ConnectionText => $"{LocalPortText}  ⇄  {RemotePortText}";

    /// <summary>This device's port, when it's in its port list - its state, speed and traffic.</summary>
    public PortItemViewModel? LocalPort { get; }

    public bool HasLocalPort => LocalPort is not null;

    public string LocalPortStateText => LocalPort is null ? string.Empty : $"{LocalPort.StatusText} · {LocalPort.SpeedText}";

    /// <summary>How it's tied to LibreNMS: "In LibreNMS", "Matched by name", ...</summary>
    public string MatchText => _neighbour.Match switch
    {
        NeighbourMatchKind.LibreNms => "In LibreNMS",
        NeighbourMatchKind.Name => "Matched by name",
        NeighbourMatchKind.Mac => "Matched by MAC",
        _ => "Not in LibreNMS",
    };

    /// <summary>Who reports the connection, and how: "Seen by this device · LLDP" or "Seen by dist-sw-01 · LLDP".</summary>
    public string SourceText
    {
        get
        {
            var who = _neighbour.Source == NeighbourSource.ThisDevice ? ThisDeviceName : Name;
            var protocols = _neighbour.Protocols.Count > 0 ? " · " + string.Join(" + ", _neighbour.Protocols) : string.Empty;
            return $"Seen by {who}{protocols}";
        }
    }

    /// <summary>Still being seen - false once LibreNMS stops seeing it but keeps the record.</summary>
    public bool IsActive => _neighbour.Active;

    public bool IsStale => !_neighbour.Active;

    private bool _isHighlighted;

    /// <summary>Hovered in the graph or its card is - the other one highlights it too.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }

    /// <summary>Order: live, known devices first.</summary>
    public int SortRank => (IsActive ? 0 : 2) + (IsKnownDevice ? 0 : 1);

    /// <summary>The neighbour's port once looked up (a connection only the neighbour reports).</summary>
    public void SetRemotePortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == _remotePortName)
        {
            return;
        }

        _remotePortName = name;
        OnPropertyChanged(nameof(RemotePortText));
        OnPropertyChanged(nameof(ConnectionText));
    }

    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        || LocalPortText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || RemotePortText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || MatchText.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static string? TitleCase(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : char.ToUpperInvariant(text[0]) + text[1..];
}

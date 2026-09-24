using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>How a routing row reads at a glance - drives its status dot.</summary>
public enum RoutingHealth
{
    Ok,
    Warning,
    Critical,

    /// <summary>Administratively shut down - deliberate, not a fault.</summary>
    Disabled,
}

/// <summary>
/// Backs Device Details' Network, Routing section (#53): the device's BGP
/// sessions, OSPF and OSPFv3 neighbours, and VRFs (with the interfaces in
/// each). Loaded when the window opens, since the nav item only shows for a
/// device that has any - most switches don't. Each kind loads on its own, so
/// one failing doesn't hide the others.
/// </summary>
public sealed class RoutingSectionViewModel : ObservableObject
{
    private readonly int _deviceId;
    private readonly ILibreNmsClient _client;
    private readonly ILogger _logger;
    private readonly CancellationToken _windowToken;

    private IReadOnlyList<Port> _ports = Array.Empty<Port>();
    private IReadOnlyList<Vrf> _vrfs = Array.Empty<Vrf>();
    private bool _hasLoaded;
    private bool _isLoading;
    private string? _errorMessage;

    public RoutingSectionViewModel(int deviceId, ILibreNmsClient client, ILogger logger, CancellationToken windowToken)
    {
        _deviceId = deviceId;
        _client = client;
        _logger = logger;
        _windowToken = windowToken;

        BgpSessions = new ObservableCollection<BgpSessionItemViewModel>();
        OspfNeighbours = new ObservableCollection<OspfNeighbourItemViewModel>();
        Vrfs = new ObservableCollection<VrfItemViewModel>();
    }

    public ObservableCollection<BgpSessionItemViewModel> BgpSessions { get; }

    public ObservableCollection<OspfNeighbourItemViewModel> OspfNeighbours { get; }

    public ObservableCollection<VrfItemViewModel> Vrfs { get; }

    public bool HasBgp => BgpSessions.Count > 0;

    public bool HasOspf => OspfNeighbours.Count > 0;

    public bool HasVrfs => Vrfs.Count > 0;

    public bool HasAny => HasBgp || HasOspf || HasVrfs;

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

    /// <summary>Why any part couldn't be loaded - the rest still shows.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                RaiseStateChanged();
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    /// <summary>Shows while loading and once there's something to show (or an error to see) - see the class remarks.</summary>
    public bool ShowNav => !_hasLoaded || HasAny || HasError;

    public bool ShowEmptyMessage => _hasLoaded && !IsLoading && !HasAny && !HasError;

    /// <summary>"2 BGP sessions (1 not established) - 3 OSPF neighbours - 1 VRF".</summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>();

            if (HasBgp)
            {
                var notUp = BgpSessions.Count(s => s.Health is RoutingHealth.Critical or RoutingHealth.Warning);
                parts.Add(Count(BgpSessions.Count, "BGP session") + (notUp > 0 ? $" ({notUp} not established)" : string.Empty));
            }

            if (HasOspf)
            {
                var notFull = OspfNeighbours.Count(n => n.Health is RoutingHealth.Critical or RoutingHealth.Warning);
                parts.Add(Count(OspfNeighbours.Count, "OSPF neighbour") + (notFull > 0 ? $" ({notFull} not full)" : string.Empty));
            }

            if (HasVrfs)
            {
                parts.Add(Count(Vrfs.Count, "VRF"));
            }

            return string.Join(" - ", parts);
        }
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        var errors = new List<string>();

        async Task<IReadOnlyList<T>> Try<T>(Task<IReadOnlyList<T>> request, string what)
        {
            try
            {
                return await request.ConfigureAwait(true);
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogWarning(ex, "Could not load {What} for device {DeviceId}", what, _deviceId);
                errors.Add($"{what}: {ex.ToUserMessage()}");
                return Array.Empty<T>();
            }
        }

        try
        {
            var bgpTask = Try(_client.Routing.ListBgpSessionsAsync(_deviceId, _windowToken), "BGP sessions");
            var ospfTask = Try(_client.Routing.ListOspfNeighboursAsync(_deviceId, _windowToken), "OSPF neighbours");
            var ospfv3Task = Try(_client.Routing.ListOspfv3NeighboursAsync(_deviceId, _windowToken), "OSPFv3 neighbours");
            var vrfTask = Try(_client.Routing.ListVrfsAsync(_deviceId, _windowToken), "VRFs");
            await Task.WhenAll(bgpTask, ospfTask, ospfv3Task, vrfTask).ConfigureAwait(true);

            _vrfs = vrfTask.Result;
            var vrfNames = _vrfs.ToDictionary(v => v.Id, v => v.Name ?? $"VRF {v.Id}");

            Replace(BgpSessions, bgpTask.Result
                .OrderBy(s => s.IsEstablished)
                .ThenBy(s => s.PeerAddressText, StringComparer.OrdinalIgnoreCase)
                .Select(s => new BgpSessionItemViewModel(s, s.VrfId is { } id && vrfNames.TryGetValue(id, out var name) ? name : null)));

            var portNames = PortNames();
            Replace(OspfNeighbours, ospfTask.Result.Select(n => OspfNeighbourItemViewModel.From(n, portNames))
                .Concat(ospfv3Task.Result.Select(n => OspfNeighbourItemViewModel.From(n, portNames)))
                .OrderBy(n => n.Health == RoutingHealth.Ok)
                .ThenBy(n => n.Version, StringComparer.Ordinal)
                .ThenBy(n => n.Neighbour, StringComparer.OrdinalIgnoreCase));

            RebuildVrfs();

            ErrorMessage = errors.Count == 0 ? null : string.Join("\n", errors);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _hasLoaded = true;
            IsLoading = false;
        }

        RaiseStateChanged();
    }

    /// <summary>The device's ports have (re)loaded - VRF membership and OSPF interface names come from them.</summary>
    public void OnPortsLoaded(IReadOnlyList<Port> ports)
    {
        _ports = ports;

        var portNames = PortNames();
        foreach (var neighbour in OspfNeighbours)
        {
            neighbour.ApplyPortNames(portNames);
        }

        RebuildVrfs();
    }

    private void RebuildVrfs()
    {
        Replace(Vrfs, _vrfs
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new VrfItemViewModel(v, _ports.Where(p => p.IfVrf == v.Id).Select(p => p.DisplayName).ToList())));

        RaiseStateChanged();
    }

    private Dictionary<int, string> PortNames() => _ports
        .GroupBy(p => p.PortId)
        .ToDictionary(g => g.Key, g => g.First().DisplayName);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static string Count(int count, string noun) =>
        count.ToString(CultureInfo.CurrentCulture) + " " + noun + (count == 1 ? string.Empty : "s");

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasBgp));
        OnPropertyChanged(nameof(HasOspf));
        OnPropertyChanged(nameof(HasVrfs));
        OnPropertyChanged(nameof(HasAny));
        OnPropertyChanged(nameof(ShowNav));
        OnPropertyChanged(nameof(ShowEmptyMessage));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>"3d 4h", "2h 5m", "12m" - how long a session has been up.</summary>
    internal static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return string.Empty;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    /// <summary>"openConfirm" -> "Open confirm", "twoWay" -> "Two way", "full" -> "Full".</summary>
    internal static string Words(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return "-";
        }

        var text = state.Trim();
        var builder = new System.Text.StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(text[i - 1]))
            {
                builder.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
        }

        return builder.ToString();
    }
}

/// <summary>One BGP session row.</summary>
public sealed class BgpSessionItemViewModel
{
    public BgpSessionItemViewModel(BgpSession session, string? vrfName)
    {
        Session = session;
        VrfName = vrfName;
    }

    public BgpSession Session { get; }

    public string Peer => Session.PeerAddressText;

    /// <summary>"AS65001 CLOUDFLARENET".</summary>
    public string RemoteAsText => string.IsNullOrWhiteSpace(Session.AsText)
        ? $"AS{Session.RemoteAs.ToString(CultureInfo.InvariantCulture)}"
        : $"AS{Session.RemoteAs.ToString(CultureInfo.InvariantCulture)} {Session.AsText.Trim()}";

    public string Description => Session.Description?.Trim() ?? string.Empty;

    public string? VrfName { get; }

    public string LocalAddress => RoutingText.Address(Session.LocalAddress);

    public RoutingHealth Health =>
        Session.IsAdminDown ? RoutingHealth.Disabled
        : Session.IsEstablished ? RoutingHealth.Ok
        : RoutingHealth.Critical;

    public string StateText => Session.IsAdminDown ? "Shut down" : RoutingSectionViewModel.Words(Session.State);

    /// <summary>How long it's been up - only meaningful while established.</summary>
    public string UptimeText => Session.IsEstablished ? RoutingSectionViewModel.FormatDuration(Session.EstablishedSeconds) : string.Empty;

    public string UpdatesText => $"{Session.InUpdates.ToString("N0", CultureInfo.CurrentCulture)} in / {Session.OutUpdates.ToString("N0", CultureInfo.CurrentCulture)} out";

    public string? LastError => string.IsNullOrWhiteSpace(Session.LastErrorText) ? null : Session.LastErrorText.Trim();
}

/// <summary>One OSPF or OSPFv3 neighbour row.</summary>
public sealed class OspfNeighbourItemViewModel : ObservableObject
{
    private readonly int? _portId;
    private string _interfaceName = string.Empty;

    private OspfNeighbourItemViewModel(string version, string? neighbour, string? routerId, string? state, int priority, long events, int? portId, string? context)
    {
        Version = version;
        Neighbour = RoutingText.Address(neighbour);
        RouterId = routerId?.Trim() ?? string.Empty;
        StateText = RoutingSectionViewModel.Words(state);
        Priority = priority;
        Events = events;
        _portId = portId;
        Context = string.IsNullOrWhiteSpace(context) ? null : context.Trim();

        // Full is a working adjacency; two-way is normal too, between two
        // routers that are neither DR nor BDR on a shared segment.
        Health = state?.Trim().ToLowerInvariant() switch
        {
            "full" or "twoway" => RoutingHealth.Ok,
            "down" or null or "" => RoutingHealth.Critical,
            _ => RoutingHealth.Warning,
        };
    }

    public static OspfNeighbourItemViewModel From(OspfNeighbour n, IReadOnlyDictionary<int, string> portNames)
    {
        var item = new OspfNeighbourItemViewModel("OSPF", n.IpAddress, n.RouterId, n.State, n.Priority, n.Events, n.PortId, n.ContextName);
        item.ApplyPortNames(portNames);
        return item;
    }

    public static OspfNeighbourItemViewModel From(Ospfv3Neighbour n, IReadOnlyDictionary<int, string> portNames)
    {
        var item = new OspfNeighbourItemViewModel("OSPFv3", n.Address, n.RouterId, n.State, n.Priority, n.Events, n.PortId, n.ContextName);
        item.ApplyPortNames(portNames);
        return item;
    }

    public string Version { get; }

    public string Neighbour { get; }

    public string RouterId { get; }

    public string StateText { get; }

    public RoutingHealth Health { get; }

    public int Priority { get; }

    public long Events { get; }

    public string? Context { get; }

    public string InterfaceName
    {
        get => _interfaceName;
        private set => SetProperty(ref _interfaceName, value);
    }

    public void ApplyPortNames(IReadOnlyDictionary<int, string> portNames) =>
        InterfaceName = _portId is { } id && portNames.TryGetValue(id, out var name) ? name : string.Empty;
}

/// <summary>One VRF row, with the device's interfaces in it.</summary>
public sealed class VrfItemViewModel
{
    public VrfItemViewModel(Vrf vrf, IReadOnlyList<string> interfaces)
    {
        Vrf = vrf;
        Interfaces = interfaces;
    }

    public Vrf Vrf { get; }

    public string Name => string.IsNullOrWhiteSpace(Vrf.Name) ? $"VRF {Vrf.Id}" : Vrf.Name.Trim();

    public string RouteDistinguisher => Vrf.RouteDistinguisher?.Trim() ?? string.Empty;

    public string Description => Vrf.Description?.Trim() ?? string.Empty;

    public string LocalAsText => Vrf.LocalAs is { } asn and > 0 ? "AS" + asn.ToString(CultureInfo.InvariantCulture) : string.Empty;

    public IReadOnlyList<string> Interfaces { get; }

    /// <summary>"Gi0/0, Vlan10" - or a count past a handful, with the full list in the tooltip.</summary>
    public string InterfacesText => Interfaces.Count switch
    {
        0 => string.Empty,
        <= 4 => string.Join(", ", Interfaces),
        _ => string.Join(", ", Interfaces.Take(3)) + $" and {Interfaces.Count - 3} more",
    };

    public string InterfacesToolTip => string.Join("\n", Interfaces);
}

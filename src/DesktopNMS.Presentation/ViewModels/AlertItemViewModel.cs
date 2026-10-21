using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>Progress of the on-demand fault lookup for one alert.</summary>
public enum AlertDetailState
{
    NotLoaded,
    Loading,
    Loaded,
    Failed,
}

/// <summary>
/// One row in the alert list: a rule firing on a device (#353). On LibreNMS
/// 26.10 that's every fault it keeps for them - one per matching core, port
/// or sensor - listed in <see cref="Children"/>; most rows hold one, and
/// read exactly like that alert. Before 26.10, always one alert.
/// </summary>
public sealed class AlertItemViewModel : ObservableObject
{
    private AlertGroup _group;
    private Alert _alert;
    private AlertDisplayContext _context;
    private bool _isUpdating;
    private bool _isExpanded;
    private IReadOnlyList<AlertFaultRowViewModel> _children = Array.Empty<AlertFaultRowViewModel>();

    private AlertDetail _detail = AlertDetail.Empty;
    private AlertDetailState _detailState = AlertDetailState.NotLoaded;
    private string? _detailError;

    /// <summary>
    /// The alert timestamp the loaded faults belong to. If the alert moves on,
    /// the cached faults are stale and get refetched.
    /// </summary>
    private DateTime? _detailStamp;

    public AlertItemViewModel(Alert alert, AlertDisplayContext context)
        : this(AlertGrouping.ByDeviceAndRule(new[] { alert })[0], context)
    {
    }

    public AlertItemViewModel(AlertGroup group, AlertDisplayContext context)
    {
        _group = group;
        _alert = group.Primary;
        _context = context;
        _children = BuildChildren(group, context);
    }

    /// <summary>The device and rule this row is about.</summary>
    public AlertGroupKey Key => _group.Key;

    public AlertGroup Group => _group;

    /// <summary>The row's first fault - see <see cref="AlertGroup.Primary"/>.</summary>
    public AlertKey Identity => _alert.Identity;

    /// <summary>The first fault's id. A group has one per fault - see <see cref="Contains"/>.</summary>
    public int Id => _alert.Id;

    public int FaultCount => _group.FaultCount;

    /// <summary>More than one fault for this device and rule - the row expands to list them.</summary>
    public bool IsGroup => _group.FaultCount > 1;

    /// <summary>Each fault, when <see cref="IsGroup"/>.</summary>
    public IReadOnlyList<AlertFaultRowViewModel> Children => _children;

    /// <summary>Whether the fault list is open. Only a group expands.</summary>
    public bool IsExpanded
    {
        get => _isExpanded && IsGroup;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>"8 faults", "8 faults · 3 acknowledged" - null for a single alert.</summary>
    public string? GroupSummary => !IsGroup ? null
        : _group.AcknowledgedCount > 0 && _group.UnacknowledgedCount > 0
            ? $"{_group.FaultCount} faults · {_group.AcknowledgedCount} acknowledged"
            : $"{_group.FaultCount} faults";

    /// <summary>Some faults acknowledged and some not - acknowledging one may not have covered the rest, or a new one has failed since.</summary>
    public bool IsPartlyAcknowledged => _group.IsPartlyAcknowledged;

    /// <summary>What a single fault is about - "CPU 0", a port - null for a legacy alert or a group.</summary>
    public string? EntityName => IsGroup ? null : AlertGrouping.EntityName(_alert);

    public bool HasEntityName => EntityName is not null;

    /// <summary>Whether this row holds the alert or fault <paramref name="key"/>.</summary>
    public bool Contains(AlertKey key) => _group.Faults.Any(f => f.Identity == key);

    public int DeviceId => _alert.DeviceId;

    public int RuleId => _alert.RuleId;

    public Alert Model => _alert;

    public AlertSeverity Severity => _group.Severity;

    public AlertState State => _group.State;

    /// <summary>The device name, following the user's hostname/sysName preference.</summary>
    public string DeviceName =>
        _context.DeviceNameStyle.Resolve(_context.DeviceFor(_alert.DeviceId), _alert.Hostname);

    /// <summary>The other name, shown as a subtitle. Null when it would just repeat <see cref="DeviceName"/>.</summary>
    public string? AlternateDeviceName =>
        _context.DeviceNameStyle.ResolveSecondary(_context.DeviceFor(_alert.DeviceId), _alert.Hostname, DeviceName);

    public bool HasAlternateDeviceName => AlternateDeviceName is not null;

    public string RuleName => _alert.DisplayRuleName;

    public string SeverityText => _alert.Severity.ToDisplayString();

    public string StateText => IsPartlyAcknowledged ? "Partly acknowledged" : State.ToDisplayString();

    public bool IsAcknowledged => State == AlertState.Acknowledged;

    public bool IsRecovered => State == AlertState.Recovered;

    public string? Note => _alert.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(_alert.Note);

    public string? RuleNotes => _alert.RuleNotes;

    public bool HasRuleNotes => !string.IsNullOrWhiteSpace(_alert.RuleNotes);

    public string? ProcedureUrl => string.IsNullOrWhiteSpace(_alert.Procedure) ? null : _alert.Procedure;

    public bool HasProcedure => ProcedureUrl is not null;

    /// <summary>True while an acknowledge/unmute call for this row is in flight.</summary>
    public bool IsUpdating
    {
        get => _isUpdating;
        set => SetProperty(ref _isUpdating, value);
    }

    // ------------------------------------------------------- triggering faults

    /// <summary>The rows the alert rule matched, once loaded.</summary>
    public IReadOnlyList<AlertFault> Faults => _detail.Faults;

    public IReadOnlyList<AlertFault> AddedFaults => _detail.Added;

    public IReadOnlyList<AlertFault> ResolvedFaults => _detail.Resolved;

    public bool HasFaults => _detail.Faults.Count > 0;

    /// <summary>
    /// False when the rule definition could not be read, so the values shown
    /// are the whole matched row rather than just the tested columns.
    /// </summary>
    public bool RuleConditionKnown => _detail.RuleConditionKnown;

    public bool ShowUnknownConditionNote => _detailState == AlertDetailState.Loaded && HasFaults && !RuleConditionKnown;

    public bool HasAddedFaults => _detail.Added.Count > 0;

    public bool HasResolvedFaults => _detail.Resolved.Count > 0;

    public AlertDetailState DetailState
    {
        get => _detailState;
        private set
        {
            if (SetProperty(ref _detailState, value))
            {
                OnPropertyChanged(nameof(IsLoadingDetail));
                OnPropertyChanged(nameof(HasDetailError));
                OnPropertyChanged(nameof(ShowNoFaultsMessage));
            }
        }
    }

    public bool IsLoadingDetail => _detailState == AlertDetailState.Loading;

    public bool HasDetailError => _detailState == AlertDetailState.Failed;

    /// <summary>True when the lookup succeeded but LibreNMS had nothing recorded.</summary>
    public bool ShowNoFaultsMessage => _detailState == AlertDetailState.Loaded && !HasFaults;

    public string? DetailError
    {
        get => _detailError;
        private set => SetProperty(ref _detailError, value);
    }

    /// <summary>True when the faults need fetching, either never loaded or gone stale.</summary>
    public bool NeedsDetail =>
        _detailState is AlertDetailState.NotLoaded
        || (_detailState == AlertDetailState.Loaded && _detailStamp != Stamp);

    private string? _historyText;
    private DateTime? _historyStamp;

    /// <summary>"Fired 6 times in the last 30 days · last 2 days ago" (#270) - null until loaded.</summary>
    public string? HistoryText
    {
        get => _historyText;
        private set
        {
            if (SetProperty(ref _historyText, value))
            {
                OnPropertyChanged(nameof(IsLoadingHistory));
            }
        }
    }

    public bool IsLoadingHistory => _historyText is null;

    /// <summary>The history is missing, or is from before this alert last changed.</summary>
    public bool NeedsHistory => _historyText is null || _historyStamp != Stamp;

    public void SetHistory(string text)
    {
        _historyStamp = Stamp;
        HistoryText = text;
    }

    public void BeginLoadingDetail()
    {
        DetailError = null;
        DetailState = AlertDetailState.Loading;
    }

    public void SetDetail(AlertDetail detail)
    {
        _detail = detail;
        _detailStamp = Stamp;
        DetailError = null;
        DetailState = AlertDetailState.Loaded;
        RaiseFaultsChanged();
    }

    public void SetDetailFailed(string message)
    {
        _detail = AlertDetail.Empty;
        DetailError = message;
        DetailState = AlertDetailState.Failed;
        RaiseFaultsChanged();
    }

    // ------------------------------------------------------------------- time

    /// <summary>When any of the row's faults last changed - what cached details and history are checked against.</summary>
    private DateTime? Stamp => _group.LastChanged ?? _alert.Timestamp;

    /// <summary>The alert timestamp converted to this machine's local time - for a group, when any of its faults last changed.</summary>
    public DateTime? LocalTimestamp
    {
        get
        {
            if (Stamp is not { } timestamp)
            {
                return null;
            }

            return _context.ServerTimestampsAreUtc
                ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToLocalTime()
                : timestamp;
        }
    }

    public string TimestampText => LocalTimestamp?.ToString("dd MMM HH:mm:ss") ?? "unknown";

    /// <summary>"4m", "2h 10m", "3d" - kept short so the column stays narrow.</summary>
    public string AgeText
    {
        get
        {
            if (LocalTimestamp is not { } local)
            {
                return string.Empty;
            }

            var age = DateTime.Now - local;

            if (age < TimeSpan.Zero || age.TotalMinutes < 1)
            {
                // Negative means clock skew between this machine and the server.
                return "just now";
            }

            if (age.TotalHours < 1)
            {
                return $"{(int)age.TotalMinutes}m";
            }

            if (age.TotalDays < 1)
            {
                return $"{(int)age.TotalHours}h {age.Minutes}m";
            }

            return age.TotalDays < 30
                ? $"{(int)age.TotalDays}d {age.Hours}h"
                : $"{(int)(age.TotalDays / 30)}mo";
        }
    }

    public Uri? AlertUrl => _context.Connection?.AlertUrl(_alert.Id);

    public Uri? DeviceUrl => _context.Connection?.DeviceUrl(_alert.DeviceId);

    /// <summary>Everything a search box should match against.</summary>
    public bool Matches(string term) =>
        DeviceName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (AlternateDeviceName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (_alert.Hostname?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || RuleName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || SeverityText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || StateText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (Note?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || _children.Any(c => c.EntityName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (EntityName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || _group.Faults.Any(f => f.Id.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal));

    /// <summary>Replaces the underlying alert in place so the selection survives a refresh.</summary>
    public void Update(Alert alert, AlertDisplayContext context)
        => Update(AlertGrouping.ByDeviceAndRule(new[] { alert })[0], context);

    /// <summary>Replaces the group in place so the selection and expansion survive a refresh.</summary>
    public void Update(AlertGroup group, AlertDisplayContext context)
    {
        _group = group;
        _alert = group.Primary;
        _context = context;
        _children = BuildChildren(group, context);

        RaiseAllChanged();
    }

    private static IReadOnlyList<AlertFaultRowViewModel> BuildChildren(AlertGroup group, AlertDisplayContext context)
        => group.IsSingle
            ? Array.Empty<AlertFaultRowViewModel>()
            : group.Faults.Select(f => new AlertFaultRowViewModel(f, context)).ToList();

    /// <summary>Re-applies display settings without a new alert, e.g. after the name style changed.</summary>
    public void ApplyContext(AlertDisplayContext context)
    {
        _context = context;
        RaiseAllChanged();
    }

    /// <summary>Re-evaluates the derived time properties. Called on a UI timer.</summary>
    public void RefreshAge() => OnPropertyChanged(nameof(AgeText));

    private void RaiseFaultsChanged()
    {
        OnPropertyChanged(nameof(Faults));
        OnPropertyChanged(nameof(AddedFaults));
        OnPropertyChanged(nameof(ResolvedFaults));
        OnPropertyChanged(nameof(HasFaults));
        OnPropertyChanged(nameof(HasAddedFaults));
        OnPropertyChanged(nameof(HasResolvedFaults));
        OnPropertyChanged(nameof(ShowNoFaultsMessage));
        OnPropertyChanged(nameof(RuleConditionKnown));
        OnPropertyChanged(nameof(ShowUnknownConditionNote));
    }

    private void RaiseAllChanged()
    {
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(Identity));
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(FaultCount));
        OnPropertyChanged(nameof(IsGroup));
        OnPropertyChanged(nameof(Children));
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(GroupSummary));
        OnPropertyChanged(nameof(IsPartlyAcknowledged));
        OnPropertyChanged(nameof(EntityName));
        OnPropertyChanged(nameof(HasEntityName));
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(AlternateDeviceName));
        OnPropertyChanged(nameof(HasAlternateDeviceName));
        OnPropertyChanged(nameof(RuleName));
        OnPropertyChanged(nameof(SeverityText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsAcknowledged));
        OnPropertyChanged(nameof(IsRecovered));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(RuleNotes));
        OnPropertyChanged(nameof(HasRuleNotes));
        OnPropertyChanged(nameof(ProcedureUrl));
        OnPropertyChanged(nameof(HasProcedure));
        OnPropertyChanged(nameof(LocalTimestamp));
        OnPropertyChanged(nameof(TimestampText));
        OnPropertyChanged(nameof(AgeText));
        OnPropertyChanged(nameof(AlertUrl));
        OnPropertyChanged(nameof(DeviceUrl));
        OnPropertyChanged(nameof(NeedsDetail));
    }
}

/// <summary>One fault in an expanded alert group (#353): the core, port or sensor it's about, and its own state.</summary>
public sealed class AlertFaultRowViewModel
{
    private readonly AlertDisplayContext _context;

    public AlertFaultRowViewModel(Alert fault, AlertDisplayContext context)
    {
        Fault = fault;
        _context = context;
    }

    public Alert Fault { get; }

    public AlertKey Identity => Fault.Identity;

    public int Id => Fault.Id;

    /// <summary>"CPU 0", "Gi1/0/24" - or the fault's type and id when its details don't name it.</summary>
    public string? EntityName => AlertGrouping.EntityName(Fault);

    public string DisplayName => EntityName ?? "Whole device";

    public AlertSeverity Severity => Fault.Severity;

    public AlertState State => Fault.State;

    public bool IsAcknowledged => Fault.IsAcknowledged;

    public string StateText => Fault.State.ToDisplayString();

    public string TimestampText => (Fault.Timestamp is { } timestamp
        ? _context.ServerTimestampsAreUtc ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToLocalTime() : timestamp
        : (DateTime?)null)?.ToString("dd MMM HH:mm:ss") ?? "unknown";
}

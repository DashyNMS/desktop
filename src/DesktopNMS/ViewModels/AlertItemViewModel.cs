using System;
using System.Collections.Generic;
using System.Globalization;
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

/// <summary>One row in the alert list.</summary>
public sealed class AlertItemViewModel : ObservableObject
{
    private Alert _alert;
    private AlertDisplayContext _context;
    private bool _isUpdating;

    private AlertDetail _detail = AlertDetail.Empty;
    private AlertDetailState _detailState = AlertDetailState.NotLoaded;
    private string? _detailError;

    /// <summary>
    /// The alert timestamp the loaded faults belong to. If the alert moves on,
    /// the cached faults are stale and get refetched.
    /// </summary>
    private DateTime? _detailStamp;

    public AlertItemViewModel(Alert alert, AlertDisplayContext context)
    {
        _alert = alert;
        _context = context;
    }

    public int Id => _alert.Id;

    public int DeviceId => _alert.DeviceId;

    public int RuleId => _alert.RuleId;

    public Alert Model => _alert;

    public AlertSeverity Severity => _alert.Severity;

    public AlertState State => _alert.State;

    /// <summary>The device name, following the user's hostname/sysName preference.</summary>
    public string DeviceName =>
        _context.DeviceNameStyle.Resolve(_context.DeviceFor(_alert.DeviceId), _alert.Hostname);

    /// <summary>The other name, shown as a subtitle. Null when it would just repeat <see cref="DeviceName"/>.</summary>
    public string? AlternateDeviceName =>
        _context.DeviceNameStyle.ResolveSecondary(_context.DeviceFor(_alert.DeviceId), _alert.Hostname, DeviceName);

    public bool HasAlternateDeviceName => AlternateDeviceName is not null;

    public string RuleName => _alert.DisplayRuleName;

    public string SeverityText => _alert.Severity.ToDisplayString();

    public string StateText => _alert.State.ToDisplayString();

    public bool IsAcknowledged => _alert.IsAcknowledged;

    public bool IsRecovered => _alert.State == AlertState.Recovered;

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
        || (_detailState == AlertDetailState.Loaded && _detailStamp != _alert.Timestamp);

    public void BeginLoadingDetail()
    {
        DetailError = null;
        DetailState = AlertDetailState.Loading;
    }

    public void SetDetail(AlertDetail detail)
    {
        _detail = detail;
        _detailStamp = _alert.Timestamp;
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

    /// <summary>The alert timestamp converted to this machine's local time.</summary>
    public DateTime? LocalTimestamp
    {
        get
        {
            if (_alert.Timestamp is not { } timestamp)
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
        || Id.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal);

    /// <summary>Replaces the underlying alert in place so the selection survives a refresh.</summary>
    public void Update(Alert alert, AlertDisplayContext context)
    {
        _alert = alert;
        _context = context;

        RaiseAllChanged();
    }

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

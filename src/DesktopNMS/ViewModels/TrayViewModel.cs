using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>Everything the tray shows at one moment, gathered by the app (#232).</summary>
public sealed record TraySnapshot(
    TrayConnection Connection,
    bool OnBackup,
    int Critical,
    int Warning,
    int Acknowledged,
    TimeSpan? LastChecked,
    string? NextCheck,
    IReadOnlyList<AlertItemViewModel> Alerts,
    string? UpdateReadyText);

/// <summary>One of the quick look's latest alerts.</summary>
public sealed class TrayAlertViewModel
{
    public TrayAlertViewModel(AlertItemViewModel alert)
    {
        AlertId = alert.Id;
        DeviceName = alert.DeviceName;
        RuleName = alert.RuleName;
        AgeText = alert.AgeText;
        Severity = alert.Severity;
    }

    public int AlertId { get; }

    public string DeviceName { get; }

    public string RuleName { get; }

    public string AgeText { get; }

    public AlertSeverity Severity { get; }
}

/// <summary>
/// The tray's themed menu and its quick look (#232): one view model for both,
/// refreshed from a <see cref="TraySnapshot"/> whenever the app's state
/// changes and each time either opens.
/// </summary>
public sealed class TrayViewModel : ObservableObject
{
    /// <summary>The quick look's list stops at this many.</summary>
    public const int LatestAlertCount = 3;

    private TraySnapshot _snapshot = new(TrayConnection.SignedOut, false, 0, 0, 0, null, null, Array.Empty<AlertItemViewModel>(), null);
    private TrayState _state = TrayStatus.Describe(TrayConnection.SignedOut, false, 0, 0);
    private IReadOnlyList<TrayAlertViewModel> _latestAlerts = Array.Empty<TrayAlertViewModel>();

    public TrayViewModel()
    {
        OpenCommand = Raise(() => OpenRequested);
        DashboardCommand = Raise(() => DashboardRequested);
        AlertsCommand = Raise(() => AlertsRequested);
        DevicesCommand = Raise(() => DevicesRequested);
        RefreshCommand = new RelayCommand(() => Run(RefreshRequested), () => IsSignedIn);
        SettingsCommand = Raise(() => SettingsRequested);
        SignInOrOutCommand = Raise(() => SignInOrOutRequested);
        ExitCommand = Raise(() => ExitRequested);
        InstallUpdateCommand = Raise(() => InstallUpdateRequested);
        OpenAlertCommand = new RelayCommand(p =>
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            if (p is TrayAlertViewModel alert)
            {
                AlertRequested?.Invoke(this, alert.AlertId);
            }
        });
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? DashboardRequested;

    public event EventHandler? AlertsRequested;

    public event EventHandler? DevicesRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

    /// <summary>Sign out while signed in, Sign in… while not.</summary>
    public event EventHandler? SignInOrOutRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler? InstallUpdateRequested;

    /// <summary>One of the quick look's alerts, by id.</summary>
    public event EventHandler<int>? AlertRequested;

    /// <summary>Any action taken: the quick look closes.</summary>
    public event EventHandler? CloseRequested;

    public RelayCommand OpenCommand { get; }

    public RelayCommand DashboardCommand { get; }

    public RelayCommand AlertsCommand { get; }

    public RelayCommand DevicesCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand SettingsCommand { get; }

    public RelayCommand SignInOrOutCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand InstallUpdateCommand { get; }

    public RelayCommand OpenAlertCommand { get; }

    public TrayState State => _state;

    public TrayIconKind IconKind => _state.Icon;

    public string Title => _state.Title;

    public string Detail => _state.Detail;

    public string Tooltip => _state.Tooltip;

    /// <summary>Signed in, even if the server isn't answering right now.</summary>
    public bool IsSignedIn => _snapshot.Connection is TrayConnection.Connected or TrayConnection.Unreachable or TrayConnection.TokenRejected;

    /// <summary>Connected and answering: the counts mean something.</summary>
    public bool ShowCounts => _snapshot.Connection == TrayConnection.Connected;

    /// <summary>The header's mark in grey.</summary>
    public bool IsMarkMuted => _state.Icon == TrayIconKind.NotConnected;

    /// <summary>The header's mark beats while signing in, as at launch.</summary>
    public bool IsSigningIn => _snapshot.Connection == TrayConnection.SigningIn;

    public bool IsOnBackup => ShowCounts && _snapshot.OnBackup;

    public int CriticalCount => _snapshot.Critical;

    public int WarningCount => _snapshot.Warning;

    public int AcknowledgedCount => _snapshot.Acknowledged;

    public string CriticalText => $"{_snapshot.Critical} critical";

    public string WarningText => $"{_snapshot.Warning} warning";

    public bool ShowCriticalChip => ShowCounts && _snapshot.Critical > 0;

    public bool ShowWarningChip => ShowCounts && _snapshot.Warning > 0;

    public bool ShowAllClearChip => ShowCounts && _snapshot.Critical + _snapshot.Warning == 0;

    /// <summary>The menu's Alerts item: the active count, when there is one.</summary>
    public string AlertsCountText => ShowCounts && _snapshot.Critical + _snapshot.Warning > 0
        ? (_snapshot.Critical + _snapshot.Warning).ToString(CultureInfo.CurrentCulture)
        : string.Empty;

    public IReadOnlyList<TrayAlertViewModel> LatestAlerts => _latestAlerts;

    public bool HasLatestAlerts => ShowCounts && _latestAlerts.Count > 0;

    public bool ShowNoAlerts => ShowCounts && _latestAlerts.Count == 0;

    public string ViewAllText => _snapshot.Critical + _snapshot.Warning > LatestAlertCount
        ? $"View all {_snapshot.Critical + _snapshot.Warning} alerts"
        : "View alerts";

    /// <summary>Signed out, signing in, or turned down: the quick look just says so.</summary>
    public bool ShowSignedOutMessage => !ShowCounts;

    public string SignInOrOutText => IsSignedIn ? "Sign out…" : "Sign in…";

    /// <summary>"Try again now" while the server isn't answering.</summary>
    public string RefreshText => _snapshot.Connection == TrayConnection.Unreachable ? "Try again now" : "Refresh now";

    public bool IsUpdateReady => !string.IsNullOrEmpty(_snapshot.UpdateReadyText);

    public string UpdateReadyText => _snapshot.UpdateReadyText ?? string.Empty;

    public void Update(TraySnapshot snapshot)
    {
        _snapshot = snapshot;
        _state = TrayStatus.Describe(snapshot.Connection, snapshot.OnBackup, snapshot.Critical, snapshot.Warning, snapshot.LastChecked, snapshot.NextCheck);

        // Active ones only, newest first, worst first on a tie.
        _latestAlerts = snapshot.Alerts
            .Where(a => a.State == AlertState.Active && a.Severity is AlertSeverity.Critical or AlertSeverity.Warning)
            .OrderByDescending(a => a.LocalTimestamp ?? DateTime.MinValue)
            .ThenBy(a => a.Severity == AlertSeverity.Critical ? 0 : 1)
            .Take(LatestAlertCount)
            .Select(a => new TrayAlertViewModel(a))
            .ToList();

        // Everything here derives from the snapshot.
        OnPropertyChanged(string.Empty);
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private RelayCommand Raise(Func<EventHandler?> handler) => new(() => Run(handler()));

    private void Run(EventHandler? handler)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        handler?.Invoke(this, EventArgs.Empty);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// View model behind the Rules tab (issues #21/#22/#28): lists every LibreNMS
/// alert rule and lets them be created/edited/deleted, enabled/disabled in
/// place, and filtered by severity. Like <see cref="GroupsViewModel"/>, the
/// rule list itself has no background poll - rules change far less often
/// than device/sensor state - but each row's "alerts raised" badge does
/// follow the app-wide <see cref="AlertMonitor"/> so it stays live.
/// </summary>
public sealed class RulesViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly IWindowService _windows;
    private readonly AlertMonitor _monitor;
    private readonly ILogger<RulesViewModel> _logger;

    /// <summary>Latest per-rule count of active + acknowledged alerts, kept between rule reloads so a fresh row can be stamped immediately.</summary>
    private Dictionary<int, int> _alertCounts = new();

    private bool _hasLoadedOnce;
    private bool _isBusy;
    private string? _errorMessage;
    private bool _showCritical = true;
    private bool _showWarning = true;
    private bool _showOk = true;

    public RulesViewModel(ILibreNmsClient client, ISessionService session, IWindowService windows, AlertMonitor monitor, ILogger<RulesViewModel> logger)
    {
        _client = client;
        _session = session;
        _windows = windows;
        _monitor = monitor;
        _logger = logger;

        Rules = new ObservableCollection<RuleItemViewModel>();
        Filtered = new FilteredListViewModel(Rules, (item, term) => MatchesFilter((RuleItemViewModel)item, term));
        Filtered.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Filtered.VisibleCount))
            {
                LoadState.UpdateVisibleCount(Filtered.VisibleCount);
            }
        };

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => _session.IsConnected && !IsBusy);
        AddRuleCommand = new RelayCommand(AddRule);
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        _monitor.Polled += OnPolled;
    }

    /// <summary>Raised when a row's alert badge is clicked - the main view model switches to the Alerts tab filtered to that rule.</summary>
    public event EventHandler<AlertRule>? ShowAlertsRequested;

    public ObservableCollection<RuleItemViewModel> Rules { get; }

    /// <summary>Search box (issue #28) + filtered view - see <see cref="FilteredListViewModel"/>'s own doc comment for why this is a separate class rather than an ICollectionView property declared directly here.</summary>
    public FilteredListViewModel Filtered { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand AddRuleCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    // -------------------------------------------------------------- severity filter

    public bool ShowCritical
    {
        get => _showCritical;
        set { if (SetProperty(ref _showCritical, value)) Filtered.Refresh(); }
    }

    public bool ShowWarning
    {
        get => _showWarning;
        set { if (SetProperty(ref _showWarning, value)) Filtered.Refresh(); }
    }

    public bool ShowOk
    {
        get => _showOk;
        set { if (SetProperty(ref _showOk, value)) Filtered.Refresh(); }
    }

    /// <summary>Shift-click on a severity pill: show only that severity - same gesture as the Alerts and Devices tabs.</summary>
    public void IsolateSeverity(AlertSeverity severity)
    {
        _showCritical = severity == AlertSeverity.Critical;
        _showWarning = severity == AlertSeverity.Warning;
        _showOk = severity == AlertSeverity.Ok;

        OnPropertyChanged(nameof(ShowCritical));
        OnPropertyChanged(nameof(ShowWarning));
        OnPropertyChanged(nameof(ShowOk));
        Filtered.Refresh();
    }

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

    /// <summary>Drives the loading/empty/no-matches split on the grid (issue #16).</summary>
    public ListLoadState LoadState { get; } = new();

    /// <summary>Loads the rule list once, lazily, the first time the tab is actually shown - same convention as every other tab.</summary>
    public void OnShown()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _hasLoadedOnce = true;
        _ = LoadAsync();
    }

    private bool MatchesFilter(RuleItemViewModel item, string term)
    {
        var severityAllowed = item.Severity switch
        {
            AlertSeverity.Critical => ShowCritical,
            AlertSeverity.Warning => ShowWarning,
            AlertSeverity.Ok => ShowOk,
            _ => true,
        };

        return severityAllowed && (string.IsNullOrWhiteSpace(term) || item.Matches(term));
    }

    private void ClearFilters()
    {
        _showCritical = true;
        _showWarning = true;
        _showOk = true;
        OnPropertyChanged(nameof(ShowCritical));
        OnPropertyChanged(nameof(ShowWarning));
        OnPropertyChanged(nameof(ShowOk));
        Filtered.SearchText = string.Empty;
        Filtered.Refresh();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        LoadState.BeginLoad();

        try
        {
            var rules = await _client.Rules.ListAsync().ConfigureAwait(true);

            Rules.Clear();
            foreach (var rule in rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new RuleItemViewModel(rule, EditRule, DeleteRule, ToggleDisabled, ShowAlerts);
                item.ActiveAlertCount = _alertCounts.GetValueOrDefault(rule.Id);
                Rules.Add(item);
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load alert rules");
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
            LoadState.CompleteLoad(Rules.Count, Filtered.VisibleCount);
        }
    }

    /// <summary>
    /// Re-stamps every row's alert badge from the monitor's latest poll.
    /// Counts active and acknowledged alerts - anything still raised - and
    /// ignores recovered ones. The monitor raises this on its own thread.
    /// </summary>
    private void OnPolled(object? sender, AlertPollResult result)
    {
        if (!result.Succeeded)
        {
            return;
        }

        var counts = result.Alerts
            .Where(a => a.State != AlertState.Recovered)
            .GroupBy(a => a.RuleId)
            .ToDictionary(g => g.Key, g => g.Count());

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _alertCounts = counts;
            foreach (var item in Rules)
            {
                item.ActiveAlertCount = counts.GetValueOrDefault(item.Id);
            }
        });
    }

    private void AddRule()
    {
        if (_windows.ShowAddRuleDialog())
        {
            _ = LoadAsync();
        }
    }

    private void EditRule(RuleItemViewModel item)
    {
        if (_windows.ShowEditRuleDialog(item.Rule))
        {
            _ = LoadAsync();
        }
    }

    private void ShowAlerts(RuleItemViewModel item) => ShowAlertsRequested?.Invoke(this, item.Rule);

    private void ToggleDisabled(RuleItemViewModel item) => _ = ToggleDisabledAsync(item);

    /// <summary>
    /// Flips one rule's disabled flag in place. LibreNMS has no toggle route,
    /// so this is a full edit_rule write built from the rule exactly as
    /// loaded (<see cref="AlertRuleWriteRequest.FromRule"/>), then the row is
    /// refreshed from the server rather than trusted locally.
    /// </summary>
    private async Task ToggleDisabledAsync(RuleItemViewModel item)
    {
        item.IsToggling = true;

        try
        {
            var request = AlertRuleWriteRequest.FromRule(item.Rule);
            request.Disabled = item.IsDisabled ? 0 : 1;
            await _client.Rules.UpdateAsync(request).ConfigureAwait(true);

            var fresh = await _client.Rules.GetAsync(item.Id).ConfigureAwait(true);
            if (fresh is not null)
            {
                item.Update(fresh);
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not toggle alert rule {RuleId}", item.Id);
            _windows.ShowError(item.IsDisabled ? "Enable failed" : "Disable failed", ex.ToUserMessage());
        }
        finally
        {
            item.IsToggling = false;
        }
    }

    private void DeleteRule(RuleItemViewModel item) => _ = DeleteRuleAsync(item);

    private async Task DeleteRuleAsync(RuleItemViewModel item)
    {
        if (!_windows.Confirm("Delete rule", $"Permanently delete the rule '{item.Name}' from LibreNMS? This cannot be undone."))
        {
            return;
        }

        try
        {
            await _client.Rules.DeleteAsync(item.Id).ConfigureAwait(true);
            Rules.Remove(item);
            Filtered.Refresh();
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not delete alert rule {RuleId}", item.Id);
            _windows.ShowError("Delete failed", ex.ToUserMessage());
        }
    }
}

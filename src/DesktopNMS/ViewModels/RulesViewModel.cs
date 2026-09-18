using System;
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
/// alert rule and lets them be created/edited/deleted. Like
/// <see cref="GroupsViewModel"/>, there is no background poll - rules change
/// far less often than device/sensor state, so a manual/lazy load is enough.
/// </summary>
public sealed class RulesViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly IWindowService _windows;
    private readonly ILogger<RulesViewModel> _logger;

    private bool _hasLoadedOnce;
    private bool _isBusy;
    private string? _errorMessage;

    public RulesViewModel(ILibreNmsClient client, ISessionService session, IWindowService windows, ILogger<RulesViewModel> logger)
    {
        _client = client;
        _session = session;
        _windows = windows;
        _logger = logger;

        Rules = new ObservableCollection<RuleItemViewModel>();
        Filtered = new FilteredListViewModel(Rules, (item, term) => ((RuleItemViewModel)item).Matches(term));
        Filtered.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Filtered.VisibleCount))
            {
                LoadState.UpdateVisibleCount(Filtered.VisibleCount);
            }
        };

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => _session.IsConnected && !IsBusy);
        AddRuleCommand = new RelayCommand(AddRule);
        ClearFiltersCommand = new RelayCommand(() => Filtered.SearchText = string.Empty);
    }

    public ObservableCollection<RuleItemViewModel> Rules { get; }

    /// <summary>Search box (issue #28) + filtered view - see <see cref="FilteredListViewModel"/>'s own doc comment for why this is a separate class rather than an ICollectionView property declared directly here.</summary>
    public FilteredListViewModel Filtered { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand AddRuleCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

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
                Rules.Add(new RuleItemViewModel(rule, EditRule, DeleteRule));
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

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// View model behind the Templates tab (issue #23): list/create/edit alert
/// templates. No delete here - LibreNMS's API has no delete route for
/// templates (confirmed live), so the row context menu only ever offers Edit.
/// </summary>
public sealed class TemplatesViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly IWindowService _windows;
    private readonly ILogger<TemplatesViewModel> _logger;

    private bool _hasLoadedOnce;
    private bool _isBusy;
    private string? _errorMessage;

    /// <summary>LibreNMS's built-in template, matched by name the same way its own web UI does.</summary>
    private const string DefaultTemplateName = "Default Alert Template";

    public TemplatesViewModel(ILibreNmsClient client, ISessionService session, IWindowService windows, ILogger<TemplatesViewModel> logger)
    {
        _client = client;
        _session = session;
        _windows = windows;
        _logger = logger;

        Templates = new ObservableCollection<AlertTemplateItemViewModel>();
        Filtered = new FilteredListViewModel(Templates, (item, term) => term.Length == 0 || ((AlertTemplateItemViewModel)item).Matches(term));
        Filtered.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Filtered.VisibleCount))
            {
                LoadState.UpdateVisibleCount(Filtered.VisibleCount);
            }
        };

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => _session.IsConnected && !IsBusy);
        AddTemplateCommand = new RelayCommand(AddTemplate);
        ClearFiltersCommand = new RelayCommand(() => Filtered.SearchText = string.Empty);
    }

    public ObservableCollection<AlertTemplateItemViewModel> Templates { get; }

    /// <summary>Search box (issue #28's pattern applied here too) - see <see cref="FilteredListViewModel"/>'s own doc comment for why this is a separate class rather than an ICollectionView property declared directly here.</summary>
    public FilteredListViewModel Filtered { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand AddTemplateCommand { get; }

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

    public ListLoadState LoadState { get; } = new();

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
            // Rules are fetched alongside so each row can name the rules it's
            // attached to rather than just count them.
            var templatesTask = _client.AlertTemplates.ListAsync();
            var rulesTask = _client.Rules.ListAsync();
            await Task.WhenAll(templatesTask, rulesTask).ConfigureAwait(true);

            var ruleNames = rulesTask.Result
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.First().Name ?? $"Rule {g.Key}");

            // The default template's alert_rules comes back empty from the
            // API: LibreNMS doesn't store its mappings, it derives them as
            // "every rule not attached to some other template" (see its
            // print-alert-templates.php). Same derivation here.
            var mappedRuleIds = templatesTask.Result.SelectMany(t => t.AlertRules).ToHashSet();

            Templates.Clear();
            foreach (var template in templatesTask.Result.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var attached = string.Equals(template.Name, DefaultTemplateName, StringComparison.Ordinal)
                    ? ruleNames.Where(r => !mappedRuleIds.Contains(r.Key)).Select(r => r.Value).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                    // Names in the order LibreNMS lists the ids; an id with no
                    // matching rule (deleted since) still shows, as "Rule 123",
                    // rather than silently vanishing from the count.
                    : template.AlertRules.Select(id => ruleNames.GetValueOrDefault(id, $"Rule {id}")).ToList();

                Templates.Add(new AlertTemplateItemViewModel(template, attached, EditTemplate));
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load alert templates");
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
            LoadState.CompleteLoad(Templates.Count, Filtered.VisibleCount);
        }
    }

    private void AddTemplate()
    {
        if (_windows.ShowAddAlertTemplateDialog())
        {
            _ = LoadAsync();
        }
    }

    private void EditTemplate(AlertTemplateItemViewModel item)
    {
        if (_windows.ShowEditAlertTemplateDialog(item.Template))
        {
            _ = LoadAsync();
        }
    }
}

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

    public TemplatesViewModel(ILibreNmsClient client, ISessionService session, IWindowService windows, ILogger<TemplatesViewModel> logger)
    {
        _client = client;
        _session = session;
        _windows = windows;
        _logger = logger;

        Templates = new ObservableCollection<AlertTemplateItemViewModel>();
        Filtered = new FilteredListViewModel(Templates, (item, term) => ((AlertTemplateItemViewModel)item).Matches(term));
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
            var templates = await _client.AlertTemplates.ListAsync().ConfigureAwait(true);

            Templates.Clear();
            foreach (var template in templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                Templates.Add(new AlertTemplateItemViewModel(template, EditTemplate));
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

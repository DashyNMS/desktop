using System;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the "Add/Edit alert template" dialog (see <see cref="Views.AlertTemplateEditorWindow"/>,
/// issue #23). One class serves both modes, same shape as
/// <see cref="RuleEditorViewModel"/>/<see cref="LocationEditorViewModel"/> -
/// create mode is the default; <see cref="Initialize"/> switches it into edit
/// mode. No delete anywhere in this dialog or its caller - LibreNMS's API has
/// no delete route for templates (confirmed live).
/// </summary>
public sealed class AlertTemplateEditorViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ILogger<AlertTemplateEditorViewModel> _logger;

    private AlertTemplate? _originalTemplate;
    private string _name = string.Empty;
    private string _template = string.Empty;
    private string? _templateTitle;
    private string? _titleRec;
    private bool _isBusy;
    private string? _errorMessage;

    public AlertTemplateEditorViewModel(ILibreNmsClient client, ILogger<AlertTemplateEditorViewModel> logger)
    {
        _client = client;
        _logger = logger;

        RulesPicker = new CheckablePickerViewModel();

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Template));

        _ = LoadRulesAsync();
    }

    /// <summary>Safe to call before <see cref="LoadRulesAsync"/> has finished - same reasoning as <see cref="DeviceGroupEditorViewModel.Initialize"/>.</summary>
    public void Initialize(AlertTemplate template)
    {
        _originalTemplate = template;
        Name = template.Name ?? string.Empty;
        Template = template.Template ?? string.Empty;
        TemplateTitle = template.Title;
        TitleRec = template.TitleRec;
    }

    public bool IsEditMode => _originalTemplate is not null;

    public string Title => IsEditMode ? "Edit alert template" : "Add alert template";

    public event EventHandler<bool>? RequestClose;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Template
    {
        get => _template;
        set
        {
            if (SetProperty(ref _template, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Named to avoid clashing with the dialog's own <see cref="Title"/> (the window/heading text).</summary>
    public string? TemplateTitle
    {
        get => _templateTitle;
        set => SetProperty(ref _templateTitle, value);
    }

    public string? TitleRec
    {
        get => _titleRec;
        set => SetProperty(ref _titleRec, value);
    }

    public CheckablePickerViewModel RulesPicker { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
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

    private async Task LoadRulesAsync()
    {
        try
        {
            var rules = await _client.Rules.ListAsync().ConfigureAwait(true);
            var attached = (_originalTemplate?.AlertRules ?? new System.Collections.Generic.List<int>()).ToHashSet();

            RulesPicker.Load(rules.Select(r => (r.Id, r.Name ?? $"Rule {r.Id}")), attached);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load rules for the alert template editor");
            ErrorMessage = ex.ToUserMessage();
        }
    }

    /// <summary>The one name LibreNMS's API refuses on create (its add_edit_alert_template handler hard-codes it) - it belongs to the built-in template.</summary>
    private const string ReservedName = "Default Alert Template";

    private async Task SaveAsync()
    {
        ErrorMessage = null;

        // Caught here with a plain message rather than letting the API's
        // 400 "This template name is reserved!" come back.
        var isRenamingToReserved = !string.Equals(_originalTemplate?.Name, ReservedName, StringComparison.Ordinal);
        if (string.Equals(Name.Trim(), ReservedName, StringComparison.OrdinalIgnoreCase) && isRenamingToReserved)
        {
            ErrorMessage = $"\"{ReservedName}\" is the name of LibreNMS's built-in template and can't be used for another one - choose a different name.";
            return;
        }

        IsBusy = true;

        try
        {
            var request = new AlertTemplateWriteRequest
            {
                TemplateId = _originalTemplate?.Id,
                Name = Name,
                Template = Template,
                Title = string.IsNullOrWhiteSpace(TemplateTitle) ? null : TemplateTitle,
                TitleRec = string.IsNullOrWhiteSpace(TitleRec) ? null : TitleRec,
                AlertRules = RulesPicker.CheckedIds.ToList(),
            };

            if (_originalTemplate is not null)
            {
                await _client.AlertTemplates.UpdateAsync(request).ConfigureAwait(true);
            }
            else
            {
                await _client.AlertTemplates.CreateAsync(request).ConfigureAwait(true);
            }

            RequestClose?.Invoke(this, true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not save alert template {TemplateName}", Name);
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

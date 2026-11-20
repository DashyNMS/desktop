using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One agent provider's card in Settings › Intelligence (#316): where it
/// stands and how to fix it, which executable to use, and the model and
/// reasoning a new chat starts with. The choices are part of Settings'
/// draft, saved with everything else; the status is the live provider's.
/// </summary>
public sealed class IntelligenceAgentSettingsViewModel : ObservableObject
{
    private readonly AppSettings _draft;

    internal IntelligenceAgentSettingsViewModel(IntelligenceProviderViewModel provider, AppSettings draft)
    {
        Provider = provider;
        _draft = draft;
        BrowseCommand = new RelayCommand(Browse);
        CopySetupCommand = new RelayCommand(() =>
        {
            if (Provider.SetupCommandText is { } command) UiPlatform.Clipboard.SetText(command);
        }, () => Provider.HasSetupCommand);
        CheckCommand = new AsyncRelayCommand(() => Provider.CheckAsync(CancellationToken.None, ExecutablePath), () => !Provider.IsChecking);
        ((INotifyPropertyChanged)Provider).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IntelligenceProviderViewModel.Status))
            {
                CopySetupCommand.RaiseCanExecuteChanged();
                CheckCommand.RaiseCanExecuteChanged();
            }
        };
        Provider.Models.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DefaultModel));
            OnPropertyChanged(nameof(DefaultEfforts));
        };
    }

    /// <summary>The live provider: status, version, account, models.</summary>
    public IntelligenceProviderViewModel Provider { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand CopySetupCommand { get; }

    /// <summary>Checks with the executable as entered here, before it's saved.</summary>
    public AsyncRelayCommand CheckCommand { get; }

    private IntelligenceProviderSettings Chosen
    {
        get
        {
            if (!_draft.IntelligenceProviders.TryGetValue(Provider.Id, out var chosen))
            {
                chosen = new IntelligenceProviderSettings();
                _draft.IntelligenceProviders[Provider.Id] = chosen;
            }

            return chosen;
        }
    }

    /// <summary>Empty to find the installed agent; a path when it's somewhere DashyNMS doesn't look.</summary>
    public string? ExecutablePath
    {
        get => Chosen.ExecutablePath;
        set
        {
            var path = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
            if (Chosen.ExecutablePath == path) return;
            Chosen.ExecutablePath = path;
            OnPropertyChanged();
        }
    }

    /// <summary>The model new chats start with on this provider; the first listed when unset.</summary>
    public string? DefaultModel
    {
        get => Chosen.Model ?? Provider.Models.FirstOrDefault()?.Id;
        set
        {
            if (value is null || Chosen.Model == value) return;
            Chosen.Model = value;
            if (Chosen.Effort is { } effort && !DefaultEfforts.Any(e => e.Key == effort)) Chosen.Effort = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DefaultEfforts));
            OnPropertyChanged(nameof(DefaultEffort));
        }
    }

    /// <summary>The default model's reasoning levels, with "the model's default" first.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> DefaultEfforts
    {
        get
        {
            var model = Provider.Models.FirstOrDefault(m => m.Id == DefaultModel);
            var levels = new List<KeyValuePair<string, string>> { new(string.Empty, model?.DefaultEffort is { } d ? $"Model default ({IntelligenceModelOption.EffortName(d)})" : "Model default") };
            levels.AddRange((model?.Efforts ?? Array.Empty<string>()).Select(e => new KeyValuePair<string, string>(e, IntelligenceModelOption.EffortName(e))));
            return levels;
        }
    }

    public bool HasDefaultEfforts => DefaultEfforts.Count > 1;

    public string DefaultEffort
    {
        get => Chosen.Effort ?? string.Empty;
        set
        {
            var effort = string.IsNullOrEmpty(value) ? null : value;
            if (Chosen.Effort == effort) return;
            Chosen.Effort = effort;
            OnPropertyChanged();
        }
    }

    private void Browse()
    {
        var filter = OperatingSystem.IsWindows()
            ? "Programs (*.exe;*.cmd)|*.exe;*.cmd|All files (*.*)|*.*"
            : "All files (*)|*";
        if (UiPlatform.FileDialogs.PickFileToOpen(new FileDialogOptions(filter, Title: $"Choose the {Provider.Name} executable")) is { } path)
        {
            ExecutablePath = path;
            CheckCommand.Execute(null);
        }
    }
}

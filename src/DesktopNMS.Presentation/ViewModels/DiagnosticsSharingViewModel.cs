using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

public sealed class DiagnosticsSharingViewModel : ObservableObject
{
    private bool _shareErrors;
    private bool _shareUsage;

    public DiagnosticsSharingViewModel()
    {
        AllowSelectedCommand = new RelayCommand(AllowSelected, () => Choice is null && (ShareErrors || ShareUsage));
        DeclineCommand = new RelayCommand(() => Complete(DiagnosticsSharingPreference.None), () => Choice is null);
    }

    public bool ShareErrors
    {
        get => _shareErrors;
        set { if (SetProperty(ref _shareErrors, value)) AllowSelectedCommand.RaiseCanExecuteChanged(); }
    }

    public bool ShareUsage
    {
        get => _shareUsage;
        set { if (SetProperty(ref _shareUsage, value)) AllowSelectedCommand.RaiseCanExecuteChanged(); }
    }

    public DiagnosticsSharingPreference? Choice { get; private set; }
    public RelayCommand AllowSelectedCommand { get; }
    public RelayCommand DeclineCommand { get; }
    public event EventHandler<DiagnosticsSharingPreference>? RequestClose;

    private void AllowSelected()
    {
        if (!ShareErrors && !ShareUsage) return;
        Complete((ShareErrors, ShareUsage) switch
        {
            (true, true) => DiagnosticsSharingPreference.ErrorsAndUsage,
            (true, false) => DiagnosticsSharingPreference.ErrorsOnly,
            _ => DiagnosticsSharingPreference.UsageOnly
        });
    }

    private void Complete(DiagnosticsSharingPreference preference)
    {
        if (Choice is not null) return;
        Choice = preference;
        AllowSelectedCommand.RaiseCanExecuteChanged();
        DeclineCommand.RaiseCanExecuteChanged();
        RequestClose?.Invoke(this, preference);
    }
}

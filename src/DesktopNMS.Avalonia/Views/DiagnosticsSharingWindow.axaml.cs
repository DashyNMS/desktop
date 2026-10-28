using Avalonia.Controls;
using DesktopNMS.Core.Configuration;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class DiagnosticsSharingWindow : Window
{
    private readonly DiagnosticsSharingViewModel _viewModel = new();

    public DiagnosticsSharingWindow() : this(false) { }

    public DiagnosticsSharingWindow(bool shareErrors)
    {
        _viewModel.ShareErrors = shareErrors;
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.RequestClose += (_, choice) => Close(choice);
    }

    public async Task<DiagnosticsSharingPreference?> ChooseAsync(Window? owner)
    {
        if (owner is { IsVisible: true })
            return await ShowDialog<DiagnosticsSharingPreference?>(owner);

        var completion = new TaskCompletionSource<DiagnosticsSharingPreference?>();
        Closed += (_, _) => completion.TrySetResult(_viewModel.Choice);
        Show();
        return await completion.Task;
    }
}

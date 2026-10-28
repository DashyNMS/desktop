using Avalonia.Controls;
using DesktopNMS.Services;

namespace DesktopNMS.Views;

/// <summary>One progress popup for an explicit update, regardless of its entry point.</summary>
public partial class UpdateProgressWindow : Window
{
    private bool _finished;

    public UpdateProgressWindow()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (!_finished && args.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
                args.Cancel = true;
        };
    }

    public void SetProgress(UpdateProgress progress) => this.FindControl<TextBlock>("StageText")!.Text = progress.Description;

    public void Finish()
    {
        _finished = true;
        Close();
    }
}

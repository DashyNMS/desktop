using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DesktopNMS.Services;
using DesktopNMS.Views;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

public sealed class UpdateProgressWindowTests
{
    [AvaloniaFact]
    public void Progress_displays_the_current_stage_and_stays_open_until_the_update_finishes()
    {
        var window = new UpdateProgressWindow();
        window.SetProgress(new UpdateProgress(UpdateStage.Preparing));
        window.Show();
        Assert.Equal("Verifying and preparing the update…", window.FindControl<TextBlock>("StageText")!.Text);
        window.Close();
        Assert.True(window.IsVisible);
        window.Finish();
        Assert.False(window.IsVisible);
    }
    [AvaloniaTheory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public void Progress_does_not_cancel_application_shutdown(WindowCloseReason reason)
    {
        var window = new TestProgressWindow();
        var closing = (WindowClosingEventArgs)Activator.CreateInstance(
            typeof(WindowClosingEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new object[] { reason, true }, null)!;
        window.RaiseClosing(closing);
        Assert.False(closing.Cancel);
        window.Finish();
    }

    private sealed class TestProgressWindow : UpdateProgressWindow
    {
        public void RaiseClosing(WindowClosingEventArgs args) => OnClosing(args);
    }

}

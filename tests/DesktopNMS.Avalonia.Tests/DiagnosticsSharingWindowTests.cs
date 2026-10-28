using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Views;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

public sealed class DiagnosticsSharingWindowTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_the_prompt_keeps_both_boxes_unchecked_and_returns_no_decision(bool modal)
    {
        var owner = ShowOwner(modal);
        var window = new DiagnosticsSharingWindow();
        try
        {
            var decision = window.ChooseAsync(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.False(FindCheckBox(window, "Share error reports").IsChecked);
            Assert.False(FindCheckBox(window, "Share usage and performance metrics").IsChecked);
            Assert.False(FindButton(window, "Allow selected").IsEffectivelyEnabled);
            Assert.True(FindButton(window, "Don't share").IsEffectivelyEnabled);
            window.Close();
            Assert.Null(await decision);
        }
        finally { window.Close(); owner?.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_decline_returns_none_even_when_the_checkboxes_were_checked(bool modal)
    {
        var owner = ShowOwner(modal);
        var window = new DiagnosticsSharingWindow();
        try
        {
            var decision = window.ChooseAsync(owner);
            Click(window, FindCheckBox(window, "Share error reports"));
            Click(window, FindCheckBox(window, "Share usage and performance metrics"));
            Click(window, FindButton(window, "Don't share"));
            Assert.Equal(DiagnosticsSharingPreference.None, await decision);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); owner?.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, true, false, DiagnosticsSharingPreference.ErrorsOnly)]
    [InlineData(false, false, true, DiagnosticsSharingPreference.UsageOnly)]
    [InlineData(false, true, true, DiagnosticsSharingPreference.ErrorsAndUsage)]
    [InlineData(true, true, false, DiagnosticsSharingPreference.ErrorsOnly)]
    [InlineData(true, false, true, DiagnosticsSharingPreference.UsageOnly)]
    [InlineData(true, true, true, DiagnosticsSharingPreference.ErrorsAndUsage)]
    public async Task Clicking_checkboxes_and_allow_returns_exactly_the_selected_categories(
        bool modal, bool errors, bool usage, DiagnosticsSharingPreference expected)
    {
        var owner = ShowOwner(modal);
        var window = new DiagnosticsSharingWindow();
        try
        {
            var decision = window.ChooseAsync(owner);
            if (errors) Click(window, FindCheckBox(window, "Share error reports"));
            if (usage) Click(window, FindCheckBox(window, "Share usage and performance metrics"));
            Assert.Equal(errors, FindCheckBox(window, "Share error reports").IsChecked);
            Assert.Equal(usage, FindCheckBox(window, "Share usage and performance metrics").IsChecked);
            var allow = FindButton(window, "Allow selected");
            Assert.True(allow.IsEffectivelyEnabled);
            Click(window, allow);
            Assert.Equal(expected, await decision);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); owner?.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_after_checking_a_box_still_returns_no_decision(bool modal)
    {
        var owner = ShowOwner(modal);
        var window = new DiagnosticsSharingWindow();
        try
        {
            var decision = window.ChooseAsync(owner);
            Click(window, FindCheckBox(window, "Share error reports"));
            window.Close();
            Assert.Null(await decision);
        }
        finally { window.Close(); owner?.Close(); }
    }

    [AvaloniaFact]
    public async Task Updated_prompt_preserves_error_choice_without_preselecting_broader_usage()
    {
        var window = new DiagnosticsSharingWindow(shareErrors: true);
        try
        {
            var decision = window.ChooseAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(FindCheckBox(window, "Share error reports").IsChecked);
            Assert.False(FindCheckBox(window, "Share usage and performance metrics").IsChecked);
            window.Close();
            Assert.Null(await decision);
        }
        finally { window.Close(); }
    }

    private static Window? ShowOwner(bool show)
    {
        if (!show) return null;
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        return owner;
    }

    private static CheckBox FindCheckBox(Window window, string content) =>
        Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), checkBox => Equals(checkBox.Content, content));

    private static Button FindButton(Window window, string content) =>
        Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, content));

    private static void Click(Window window, Control control)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}

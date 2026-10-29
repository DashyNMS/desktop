using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DesktopNMS.Infrastructure;
using DesktopNMS.Views;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

public sealed class CsvExportButtonTests
{
    [AvaloniaFact]
    public void Export_scope_is_shown_on_the_button_that_receives_the_pointer()
    {
        var control = new CsvExportButton
        {
            ScopeHint = "Export the current page of Graylog messages",
            Export = new CsvExport("graylog", new[] { "Message" }, () => Array.Empty<IReadOnlyList<string>>()),
        };
        var window = new Window { Content = control };
        window.Show();
        try
        {
            var button = Assert.Single(control.GetVisualDescendants().OfType<Button>());
            Assert.Equal(control.ScopeHint, ToolTip.GetTip(button));
            Assert.True(button.IsEnabled);
            control.ScopeHint = "Export loaded events matching the search";
            Assert.Equal(control.ScopeHint, ToolTip.GetTip(button));
        }
        finally
        {
            window.Close();
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.Views;

/// <summary>
/// The share icon on a table's toolbar: click for Copy as CSV / Save as
/// CSV..., the same menu the Alerts tab has, for whatever <see cref="Export"/>
/// the table's view model provides.
/// </summary>
public sealed class CsvExportButton : Button
{
    public static readonly DependencyProperty ExportProperty = DependencyProperty.Register(
        nameof(Export),
        typeof(CsvExport),
        typeof(CsvExportButton),
        new PropertyMetadata(null, (d, _) => ((CsvExportButton)d).BuildMenu()));

    public CsvExportButton()
    {
        SetResourceReference(StyleProperty, "IconButtonStyle");
        ToolTip = "Export the rows shown";
        Content = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
        };
    }

    public CsvExport? Export
    {
        get => (CsvExport?)GetValue(ExportProperty);
        set => SetValue(ExportProperty, value);
    }

    private void BuildMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, PlacementTarget = this };
        menu.Items.Add(new MenuItem { Header = "Copy as CSV", Command = Export?.CopyCommand });
        menu.Items.Add(new MenuItem { Header = "Save as CSV...", Command = Export?.SaveCommand });
        ContextMenu = menu;
        IsEnabled = Export is not null;
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (ContextMenu is { } menu)
        {
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }
    }
}

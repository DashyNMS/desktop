using Avalonia;
using Avalonia.Controls;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.Views;

public partial class CsvExportButton : UserControl
{
    public static readonly StyledProperty<CsvExport?> ExportProperty =
        AvaloniaProperty.Register<CsvExportButton, CsvExport?>(nameof(Export));

    public CsvExportButton()
    {
        InitializeComponent();
    }

    /// <summary>The table's Copy and Save commands.</summary>
    public CsvExport? Export
    {
        get => GetValue(ExportProperty);
        set => SetValue(ExportProperty, value);
    }
}

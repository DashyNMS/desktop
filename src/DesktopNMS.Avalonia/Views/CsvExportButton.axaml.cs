using Avalonia;
using Avalonia.Controls;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.Views;

public partial class CsvExportButton : UserControl
{
    public static readonly StyledProperty<CsvExport?> ExportProperty =
        AvaloniaProperty.Register<CsvExportButton, CsvExport?>(nameof(Export));

    public static readonly StyledProperty<string> ScopeHintProperty =
        AvaloniaProperty.Register<CsvExportButton, string>(nameof(ScopeHint), "Export the rows shown");

    /// <summary>Explains any paging or loading limit at the actual button.</summary>
    public string ScopeHint
    {
        get => GetValue(ScopeHintProperty);
        set => SetValue(ScopeHintProperty, value);
    }

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

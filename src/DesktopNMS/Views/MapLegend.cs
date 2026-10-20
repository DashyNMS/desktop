namespace DesktopNMS.Views;

/// <summary>The Geographical map legend's colours - the same brushes the pins are drawn with.</summary>
public static class MapLegend
{
    public static System.Windows.Media.Brush Up => Converters.SeverityToBrushConverter.Ok;

    public static System.Windows.Media.Brush Down => Converters.SeverityToBrushConverter.Critical;

    public static System.Windows.Media.Brush Maintenance => Converters.SeverityToBrushConverter.Maintenance;

    public static System.Windows.Media.Brush Inactive => Converters.SeverityToBrushConverter.Unknown;
}

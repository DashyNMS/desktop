using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>Device Details' Graphs.</summary>
public partial class GraphsSection : UserControl
{
    public GraphsSection()
    {
        InitializeComponent();

        // Tunnelling, so a shift-click on a legend entry is seen before it toggles.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Shift-clicking a legend entry shows only that series, as the severity pills do.</summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Shift
            && (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { DataContext: GraphLegendItemViewModel item } button
            && button.Classes.Contains("legendItem"))
        {
            item.OnlyCommand.Execute(null);
            e.Handled = true;
        }
    }
}

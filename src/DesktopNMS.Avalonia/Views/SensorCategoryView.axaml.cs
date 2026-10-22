using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>One Health category's sensors - see <see cref="SensorCategoryViewModel"/>.</summary>
public partial class SensorCategoryView : UserControl
{
    public SensorCategoryView()
    {
        InitializeComponent();

        // Tunnelling, so a shift-click on a status count is seen before it toggles.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Shift-clicking a status count shows only that status instead of toggling it.</summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Shift
            && DataContext is SensorCategoryViewModel viewModel
            && (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is { Tag: AlertSeverity severity } badge
            && badge.Classes.Contains("severity"))
        {
            viewModel.IsolateSeverity(severity);
            e.Handled = true;
        }
    }
}

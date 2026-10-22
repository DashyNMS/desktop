using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class RulesView : UserControl
{
    public RulesView()
    {
        InitializeComponent();

        // The selection drives the toolbar's Enable / Disable for several rules (#144).
        RulesGrid.SelectionChanged += (_, _) =>
            (DataContext as RulesViewModel)?.SetSelection(RulesGrid.SelectedItems.OfType<RuleItemViewModel>());

        // Tunnelling, so a shift-click on a severity pill is seen before it toggles.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>The notifications choice depends on what's already chosen, so it's built as the menu opens (#268).</summary>
    private void OnRulesMenuOpening(object? sender, CancelEventArgs e) => (DataContext as RulesViewModel)?.RefreshNotificationMenu();

    /// <summary>Shift-click on a severity pill shows only that severity, as on the Alerts tab.</summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Shift
            && DataContext is RulesViewModel viewModel
            && (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is { Tag: AlertSeverity severity } pill
            && pill.Classes.Contains("severity"))
        {
            viewModel.IsolateSeverity(severity);
            e.Handled = true;
        }
    }
}

using Avalonia.Controls;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Health tab - see <see cref="HealthViewModel"/>.</summary>
public partial class HealthView : UserControl
{
    public HealthView()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in the showing category's search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        var category = (DataContext as HealthViewModel)?.SelectedCategory switch
        {
            HealthCategory.Signal => SignalCategoryView,
            HealthCategory.Temperature => TemperatureCategoryView,
            HealthCategory.FanSpeed => FanSpeedCategoryView,
            HealthCategory.State => StateCategoryView,
            _ => DbmCategoryView,
        };
        category.FocusSearch();
    }
}

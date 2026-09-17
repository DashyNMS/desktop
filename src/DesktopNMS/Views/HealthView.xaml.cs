using System.Windows.Controls;
using DesktopNMS.Core.Configuration;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class HealthView : UserControl
{
    public HealthView()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in whichever category sub-tab's search box is showing. Called on Ctrl+F.</summary>
    public void FocusSearch()
    {
        if (DataContext is not HealthViewModel viewModel)
        {
            return;
        }

        switch (viewModel.SelectedCategory)
        {
            case HealthCategory.Signal:
                SignalCategoryView.FocusSearch();
                break;
            case HealthCategory.Temperature:
                TemperatureCategoryView.FocusSearch();
                break;
            case HealthCategory.FanSpeed:
                FanSpeedCategoryView.FocusSearch();
                break;
            default:
                DbmCategoryView.FocusSearch();
                break;
        }
    }

    /// <summary>
    /// All four categories share one "Health.Sensors" layout (see
    /// SensorCategoryView.xaml.cs) - their columns are identical, just
    /// filtered differently - so the saved layout is applied to all four up
    /// front, and captured back from whichever one is currently selected
    /// when this tab's layout is saved.
    /// </summary>
    public void ApplyGridLayout(GridLayout? layout)
    {
        DbmCategoryView.ApplyGridLayout(layout);
        SignalCategoryView.ApplyGridLayout(layout);
        TemperatureCategoryView.ApplyGridLayout(layout);
        FanSpeedCategoryView.ApplyGridLayout(layout);
    }

    public GridLayout? CaptureGridLayout()
    {
        if (DataContext is not HealthViewModel viewModel)
        {
            return DbmCategoryView.CaptureGridLayout();
        }

        return viewModel.SelectedCategory switch
        {
            HealthCategory.Signal => SignalCategoryView.CaptureGridLayout(),
            HealthCategory.Temperature => TemperatureCategoryView.CaptureGridLayout(),
            HealthCategory.FanSpeed => FanSpeedCategoryView.CaptureGridLayout(),
            _ => DbmCategoryView.CaptureGridLayout(),
        };
    }
}

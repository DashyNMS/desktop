using System.Windows.Controls;
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
}

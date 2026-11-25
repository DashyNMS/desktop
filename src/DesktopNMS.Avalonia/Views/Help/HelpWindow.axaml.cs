using Avalonia.Controls;
using Avalonia.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Help;

public partial class HelpWindow : Window
{
    private readonly HelpViewModel? _model;

    /// <summary>For the designer.</summary>
    public HelpWindow()
    {
        InitializeComponent();
    }

    public HelpWindow(HelpViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        Closed += (_, _) => model.Dispose();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.F && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta)
        {
            Help.FocusSearch();
            e.Handled = true;
        }
        else if (!e.Handled && e.Key == Key.Escape && _model is { IsSearching: false })
        {
            Close();
            e.Handled = true;
        }
    }
}

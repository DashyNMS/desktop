using Avalonia.Controls;
using Avalonia.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>Device Details' Neighbours: the graph and a card per neighbour, highlighted together.</summary>
public partial class NeighboursSection : UserControl
{
    public NeighboursSection()
    {
        InitializeComponent();
    }

    private void OnCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: DeviceNeighbourItemViewModel item })
        {
            item.IsHighlighted = true;
        }
    }

    private void OnCardPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: DeviceNeighbourItemViewModel item })
        {
            item.IsHighlighted = false;
        }
    }
}

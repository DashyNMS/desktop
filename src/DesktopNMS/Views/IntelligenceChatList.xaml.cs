using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Intelligence chat list (#327): saved chats, grouped by when they were last used.</summary>
public partial class IntelligenceChatList : UserControl
{
    public IntelligenceChatList()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is IntelligenceChatListViewModel chats) ApplyChatGrouping(chats);
        };
    }

    private void OnChatNameKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not IntelligenceChatListViewModel chats) return;
        if (e.Key == Key.Enter && chats.SaveChatNameCommand.CanExecute(null))
        {
            chats.SaveChatNameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            chats.CancelChatNameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.InvokeAsync(() => { if (box.IsVisible) { box.Focus(); box.SelectAll(); } });
    }

    /// <summary>
    /// The chat list grouped by activity - Pinned, Today, Yesterday… - live,
    /// so a chat moves group when it's used or the day turns (a view concern
    /// since #249).
    /// </summary>
    private static void ApplyChatGrouping(IntelligenceChatListViewModel chats)
    {
        var source = CollectionViewSource.GetDefaultView(chats.GroupedChats);
        if (source is not ListCollectionView view || view.GroupDescriptions.Count > 0)
        {
            return;
        }

        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IntelligenceChatItemViewModel.ActivityGroup)) { CustomSort = new ChatActivityGroupOrder() });
        view.LiveGroupingProperties.Add(nameof(IntelligenceChatItemViewModel.ActivityGroup));
        view.IsLiveGrouping = true;
    }

    /// <summary>Groups in their own order (Pinned first, then most recent), not alphabetically.</summary>
    private sealed class ChatActivityGroupOrder : System.Collections.IComparer
    {
        public int Compare(object? x, object? y) =>
            ((x as CollectionViewGroup)?.Name as IntelligenceChatActivityGroup)?.Order.CompareTo(
                ((y as CollectionViewGroup)?.Name as IntelligenceChatActivityGroup)?.Order ?? 0) ?? 0;
    }
}

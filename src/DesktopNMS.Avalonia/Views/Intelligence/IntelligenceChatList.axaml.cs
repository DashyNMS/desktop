using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Intelligence;

/// <summary>A heading row in the chat list: "Pinned", "Today", "Last week"…</summary>
public sealed record ChatGroupHeading(string Label);

/// <summary>
/// The Intelligence chat list (#327). Avalonia's ListBox has no grouping, so
/// the rows are the chats in their sorted order with a heading row before
/// each change of group, rebuilt as chats are used or the day turns.
/// Headings can't be selected; the chat menus (rename, pin) are built here
/// since a menu's popup can't see the list's own data.
/// </summary>
public partial class IntelligenceChatList : UserControl
{
    private readonly ObservableCollection<object> _rows = new();
    private readonly HashSet<IntelligenceChatItemViewModel> _observed = new();
    private IntelligenceChatListViewModel? _chats;
    private bool _syncingSelection;

    public IntelligenceChatList()
    {
        InitializeComponent();
        Chats.ItemsSource = _rows;
        Chats.ContainerPrepared += OnContainerPrepared;
        Chats.SelectionChanged += (_, _) =>
        {
            if (_syncingSelection || _chats is null)
            {
                return;
            }

            if (Chats.SelectedItem is IntelligenceChatItemViewModel chat)
            {
                _chats.SelectedChat = chat;
            }

            // The selection may have been refused (a busy chat) or landed on a heading.
            SyncSelection();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_chats is not null)
        {
            ((INotifyCollectionChanged)_chats.GroupedChats).CollectionChanged -= OnChatsChanged;
            _chats.PropertyChanged -= OnChatsPropertyChanged;
        }

        _chats = DataContext as IntelligenceChatListViewModel;
        if (_chats is not null)
        {
            ((INotifyCollectionChanged)_chats.GroupedChats).CollectionChanged += OnChatsChanged;
            _chats.PropertyChanged += OnChatsPropertyChanged;
        }

        Rebuild();
    }

    private void OnChatsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnChatsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IntelligenceChatListViewModel.SelectedChat))
        {
            SyncSelection();
        }
        else if (e.PropertyName is nameof(IntelligenceChatListViewModel.CanManageChats) or nameof(IntelligenceChatListViewModel.IsBusy))
        {
            UpdateEnabled();
        }
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IntelligenceChatItemViewModel.ActivityGroup))
        {
            Dispatcher.UIThread.Post(Rebuild);
        }
        else if (e.PropertyName == nameof(IntelligenceChatItemViewModel.IsRenaming) && sender is IntelligenceChatItemViewModel { IsRenaming: true } chat)
        {
            // Into the name box, all selected, once it's showing.
            Dispatcher.UIThread.Post(() =>
            {
                if (Chats.ContainerFromItem(chat)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box => box.Classes.Contains("chatName")) is { } box)
                {
                    box.Focus();
                    box.SelectAll();
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private void Rebuild()
    {
        var chats = _chats?.GroupedChats.OfType<IntelligenceChatItemViewModel>().ToList() ?? [];

        foreach (var gone in _observed.Except(chats).ToList())
        {
            gone.PropertyChanged -= OnChatPropertyChanged;
            _observed.Remove(gone);
        }

        foreach (var added in chats.Where(_observed.Add))
        {
            added.PropertyChanged += OnChatPropertyChanged;
        }

        var rows = new List<object>();
        string? group = null;
        foreach (var chat in chats)
        {
            if (chat.ActivityGroup.Label != group)
            {
                group = chat.ActivityGroup.Label;
                rows.Add(new ChatGroupHeading(group));
            }

            rows.Add(chat);
        }

        _syncingSelection = true;
        _rows.Clear();
        foreach (var row in rows)
        {
            _rows.Add(row);
        }

        _syncingSelection = false;
        SyncSelection();
    }

    private void SyncSelection()
    {
        _syncingSelection = true;
        Chats.SelectedItem = _chats?.SelectedChat is { } chat && _rows.Contains(chat) ? chat : null;
        _syncingSelection = false;
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not ListBoxItem container)
        {
            return;
        }

        var isHeading = container.DataContext is ChatGroupHeading;
        container.Classes.Set("heading", isHeading);
        container.Focusable = !isHeading;
        container.IsHitTestVisible = !isHeading;
        container.IsEnabled = isHeading || _chats?.CanManageChats != false;
        ToolTip.SetTip(container, !isHeading && _chats?.IsBusy == true ? "Finish or stop the current answer before switching chats" : null);
        container.ContextMenu = isHeading ? null : ChatMenu(container);
    }

    private void UpdateEnabled()
    {
        foreach (var container in Chats.GetRealizedContainers().OfType<ListBoxItem>())
        {
            if (container.DataContext is IntelligenceChatItemViewModel)
            {
                container.IsEnabled = _chats?.CanManageChats != false;
                ToolTip.SetTip(container, _chats?.IsBusy == true ? "Finish or stop the current answer before switching chats" : null);
            }
        }
    }

    /// <summary>Rename and pin, for the chat under the pointer.</summary>
    private ContextMenu ChatMenu(ListBoxItem container)
    {
        var rename = new MenuItem { Header = "Rename chat" };
        var pin = new MenuItem();
        var menu = new ContextMenu { Items = { rename, pin } };
        menu.Opening += (_, _) =>
        {
            if (container.DataContext is IntelligenceChatItemViewModel chat && _chats is not null)
            {
                pin.Header = chat.PinLabel;
                rename.Command = _chats.RenameChatCommand;
                rename.CommandParameter = chat;
                pin.Command = _chats.PinChatCommand;
                pin.CommandParameter = chat;
            }
        };
        return menu;
    }

    private void OnChatNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (_chats is null)
        {
            return;
        }

        if (e.Key == Key.Enter && _chats.SaveChatNameCommand.CanExecute(null))
        {
            _chats.SaveChatNameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _chats.CancelChatNameCommand.Execute(null);
            e.Handled = true;
        }
    }
}

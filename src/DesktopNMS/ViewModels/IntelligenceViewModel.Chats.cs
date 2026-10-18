using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

public sealed partial class IntelligenceViewModel
{
    private IIntelligenceChatStore _chatStore = null!;
    private readonly DispatcherTimer _chatSaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _chatScope;
    private bool _changingChat;
    private bool _chatStorageReadOnly;
    private IntelligenceChatItemViewModel? _selectedChat;
    private bool _isChatListOpen = true;
    private bool _isRenamingChat;
    private string _chatNameDraft = string.Empty;

    public ObservableCollection<IntelligenceChatItemViewModel> Chats { get; } = new();
    public RelayCommand ToggleChatListCommand { get; private set; } = null!;
    public RelayCommand RenameChatCommand { get; private set; } = null!;
    public RelayCommand SaveChatNameCommand { get; private set; } = null!;
    public RelayCommand CancelChatNameCommand { get; private set; } = null!;
    public RelayCommand DeleteChatCommand { get; private set; } = null!;
    public RelayCommand PinChatCommand { get; private set; } = null!;
    public bool CanManageChats => !IsBusy && ShowConversation && _chatScope is not null;

    public bool IsChatListOpen
    {
        get => _isChatListOpen;
        set => SetProperty(ref _isChatListOpen, value);
    }

    public bool IsRenamingChat
    {
        get => _isRenamingChat;
        private set => SetProperty(ref _isRenamingChat, value);
    }

    public string ChatNameDraft
    {
        get => _chatNameDraft;
        set { if (SetProperty(ref _chatNameDraft, value)) SaveChatNameCommand?.RaiseCanExecuteChanged(); }
    }

    public IntelligenceChatItemViewModel? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (ReferenceEquals(value, _selectedChat)) return;
            if (value is null && Chats.Count > 0 || value is not null && !Chats.Contains(value))
            {
                OnPropertyChanged();
                return;
            }
            if (IsBusy) { OnPropertyChanged(); return; }
            SaveActiveChat();
            _selectedChat = value;
            OnPropertyChanged();
            RestoreActiveChat();
            SaveActiveChat();
        }
    }

    private void InitializeChats(IIntelligenceChatStore store)
    {
        _chatStore = store;
        ToggleChatListCommand = new RelayCommand(() => IsChatListOpen = !IsChatListOpen);
        RenameChatCommand = new RelayCommand(parameter =>
        {
            if (parameter is IntelligenceChatItemViewModel target)
            {
                if (!Chats.Contains(target)) return;
                SelectedChat = target;
            }
            ChatNameDraft = SelectedChat?.Title ?? string.Empty;
            IsRenamingChat = true;
            IsChatListOpen = true;
        }, _ => CanManageChats && SelectedChat is not null);
        SaveChatNameCommand = new RelayCommand(() =>
        {
            if (SelectedChat is not { } chat) return;
            chat.Data.Title = ChatTitle(ChatNameDraft);
            chat.Data.IsRenamed = true;
            chat.Refresh();
            IsRenamingChat = false;
            SaveActiveChat();
        }, () => CanManageChats && !string.IsNullOrWhiteSpace(ChatNameDraft));
        CancelChatNameCommand = new RelayCommand(() => IsRenamingChat = false);
        DeleteChatCommand = new RelayCommand(DeleteChat, _ => CanManageChats && SelectedChat is not null);
        PinChatCommand = new RelayCommand(parameter =>
        {
            if (parameter is not IntelligenceChatItemViewModel chat || _chatScope is null || !Chats.Contains(chat)) return;
            chat.Data.IsPinned = !chat.Data.IsPinned;
            chat.Refresh();
            try { if (!_chatStorageReadOnly) _chatStore.Save(_chatScope, chat.Data, makeActive: false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intelligence: chat pin could not be saved");
                ErrorText = "Couldn't save the chat's pin.";
            }
            SortChats();
        }, _ => CanManageChats);
        _chatSaveTimer.Tick += (_, _) => SaveActiveChat();
        Context.Spotlight.CollectionChanged += (_, _) => QueueChatSave();
        _session.StateChanged += (_, _) => _dispatcher.InvokeAsync(EnsureChatScope);
        EnsureChatScope();
    }

    private string? CurrentChatScope => _credentials?.Subject is { Length: > 0 } account
        && _session.Connection is { } connection
        ? account + "\n" + connection.WebRoot.AbsoluteUri.TrimEnd('/')
        : null;

    private void EnsureChatScope()
    {
        var scope = CurrentChatScope;
        if (scope == _chatScope && (scope is null || SelectedChat is not null)) return;
        if (IsBusy) { _running?.Cancel(); return; }
        SaveActiveChat();
        _changingChat = true;
        _selectedChat = null;
        Chats.Clear();
        _chatScope = scope;
        _chatStorageReadOnly = false;
        Guid? activeId = null;
        try
        {
            if (scope is not null)
            {
                var saved = _chatStore.Load(scope);
                activeId = saved.ActiveId;
                foreach (var chat in saved.Chats) Chats.Add(new(chat));
            }
        }
        catch (Exception ex)
        {
            // Never overwrite an unreadable archive with an empty one.
            _chatStorageReadOnly = true;
            _logger.LogWarning(ex, "Intelligence: saved chats could not be read");
            ErrorText = "Couldn't read saved chats. The existing files have been kept; this chat won't be saved.";
        }
        finally { _changingChat = false; }
        if (scope is null)
        {
            RestoreActiveChat();
            OnPropertyChanged(nameof(SelectedChat));
        }
        else if (Chats.Count > 0)
        {
            SelectedChat = Chats.FirstOrDefault(chat => chat.Data.Id == activeId) ?? Chats[0];
        }
        else NewChat();
        RaiseChatCommands();
    }

    private void NewChat()
    {
        if (IsBusy || _chatScope is null) return;
        SaveActiveChat();
        // Reuse a blank draft rather than filling the list with empty chats.
        var draft = Chats.FirstOrDefault(chat => chat.Data.Messages.Count == 0
            && string.IsNullOrWhiteSpace(chat.Data.Draft) && !chat.Data.IsRenamed);
        if (draft is not null)
        {
            if (draft != SelectedChat) SelectedChat = draft;
            return;
        }
        var chat = new IntelligenceChatItemViewModel(new SavedIntelligenceChat
        {
            Model = _settings.Current.IntelligenceModel ?? SelectedModel?.Slug,
            Outfit = DashyOutfits.Pick(Random.Shared.Next(100)),
        });
        Chats.Insert(0, chat);
        SelectedChat = chat;
        SortChats();
    }

    private void RestoreActiveChat()
    {
        _changingChat = true;
        try
        {
            _conversation.RestoreHistory(SelectedChat?.Data.History ?? new());
            Messages.Clear();
            var chat = SelectedChat?.Data;
            if (chat is not null)
                foreach (var message in chat.Messages) Messages.Add(IntelligenceMessageViewModel.Restore(message));
            Question = chat?.Draft ?? string.Empty;
            CompanionOutfit = chat?.Outfit ?? DashyOutfit.Normal;
            var model = chat?.Model ?? _settings.Current.IntelligenceModel;
            SelectedModel = Models.FirstOrDefault(item => item.Slug == model)
                ?? (model is not null ? new ChatGptModel(model, ChatGptModel.NameFor(model)) : Models.FirstOrDefault());
            Context.RestoreChatContext(chat?.Spotlight ?? new(), chat?.LookedAt ?? new(), Messages.Where(m => m.IsAssistant).Select(m => m.Text));
            IsRenamingChat = false;
            if (!_chatStorageReadOnly) ErrorText = null;
            OnPropertyChanged(nameof(ShowSuggestions));
            RaiseCommands();
        }
        finally { _changingChat = false; }
    }

    private void QueueChatSave()
    {
        if (!_changingChat && _chatStore is not null && !_chatSaveTimer.IsEnabled) _chatSaveTimer.Start();
    }

    private void SaveActiveChat(bool recordActivity = false)
    {
        _chatSaveTimer.Stop();
        if (_changingChat || _chatStore is null || _chatScope is null || SelectedChat is not { } item) return;
        var chat = item.Data;
        chat.Draft = Question;
        chat.Model = SelectedModel?.Slug ?? chat.Model;
        chat.Outfit = CompanionOutfit;
        chat.History = _conversation.ExportHistory();
        chat.Messages = Messages.Select(message => message.Save()).ToList();
        chat.Spotlight = Context.Spotlight.Select(option => new SavedChatSpotlight((int)option.Kind, option.Id, option.Name)).ToList();
        chat.LookedAt = Context.LookedAt.ToList();
        // Draft saves, selection and renaming do not change conversation activity.
        if (recordActivity) chat.UpdatedAt = DateTimeOffset.UtcNow;
        item.Refresh();
        if (_chatStorageReadOnly) return;
        try { _chatStore.Save(_chatScope, chat); SortChats(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intelligence: chat could not be saved");
            ErrorText ??= "Couldn't save this chat. Check that your DashyNMS data folder is writable.";
        }
    }

    private void DeleteChat(object? parameter)
    {
        var chat = parameter as IntelligenceChatItemViewModel ?? SelectedChat;
        if (chat is null || _chatScope is null || !Chats.Contains(chat)) return;
        var scope = _chatScope;
        if (!_windows.Confirm("Delete chat?", $"Delete '{chat.Title}' and its saved messages from this PC?", "Delete", destructive: true)) return;
        if (_chatScope != scope || !Chats.Contains(chat)) return;
        try { if (!_chatStorageReadOnly) _chatStore.Delete(_chatScope, chat.Data.Id); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intelligence: chat could not be deleted");
            ErrorText = "Couldn't delete the saved chat.";
            return;
        }
        var wasSelected = ReferenceEquals(chat, SelectedChat);
        if (wasSelected) _selectedChat = null;
        Chats.Remove(chat);
        if (wasSelected)
        {
            if (Chats.Count > 0) SelectedChat = Chats[0];
            else NewChat();
        }
    }

    private void SortChats()
    {
        var sorted = Chats.OrderByDescending(chat => chat.IsPinned).ThenByDescending(chat => chat.Data.UpdatedAt).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var index = Chats.IndexOf(sorted[i]);
            if (index != i) Chats.Move(index, i);
        }
    }

    private void RaiseChatCommands()
    {
        OnPropertyChanged(nameof(CanManageChats));
        RenameChatCommand?.RaiseCanExecuteChanged();
        SaveChatNameCommand?.RaiseCanExecuteChanged();
        DeleteChatCommand?.RaiseCanExecuteChanged();
        PinChatCommand?.RaiseCanExecuteChanged();
    }

    private static string ChatTitle(string value)
    {
        var title = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return title.Length > 80 ? title[..77] + "…" : title;
    }
}

public sealed class IntelligenceChatItemViewModel(SavedIntelligenceChat data) : ObservableObject
{
    public SavedIntelligenceChat Data { get; } = data;
    public string Title => Data.Title;
    public bool IsPinned => Data.IsPinned;
    public string PinLabel => IsPinned ? "Unpin chat" : "Pin chat";
    public string LastActivity => Data.Messages.Count == 0 ? string.Empty
        : Data.UpdatedAt.ToLocalTime().ToString("g");
    public string Preview => Data.Messages.LastOrDefault(message => !string.IsNullOrWhiteSpace(message.Text))?.Text.Replace('\n', ' ') ?? "Start a conversation";
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinLabel));
        OnPropertyChanged(nameof(LastActivity));
    }
}

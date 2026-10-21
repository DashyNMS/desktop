using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using System.ComponentModel;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

public sealed partial class IntelligenceViewModel : IDisposable
{
    /// <summary>How long after the last change - typing above all - a chat is saved (#321).</summary>
    private static readonly TimeSpan ChatSaveDelay = TimeSpan.FromSeconds(3);

    /// <summary>While an answer streams, saved at most this often, so a crash loses little (#321).</summary>
    private static readonly TimeSpan StreamingSaveInterval = TimeSpan.FromSeconds(20);

    private IIntelligenceChatStore _chatStore = null!;
    private IntelligenceChatWriter _chatWriter = null!;
    private DateTimeOffset _lastChatSave = DateTimeOffset.MinValue;
    private readonly IUiTimer _chatSaveTimer = UiThread.CreateTimer(ChatSaveDelay);
    private readonly IUiTimer _chatGroupingTimer = UiThread.CreateTimer(TimeSpan.FromMinutes(1));
    private string? _chatScope;
    private bool _changingChat;
    private bool _chatStorageReadOnly;
    private IntelligenceChatItemViewModel? _selectedChat;
    private bool _isRenamingChat;
    private string _chatNameDraft = string.Empty;
    private string _selectedDetail = IntelligenceDetail.Default;

    public IReadOnlyList<KeyValuePair<string, string>> DetailOptions => IntelligenceDetailOptions.All;

    /// <summary>How much this chat's next answer says (#329) - saved with the chat as its ReplyStyle.</summary>
    public string SelectedDetail
    {
        get => _selectedDetail;
        set
        {
            if (value is null) return;
            if (IsBusy && !_changingChat) { OnPropertyChanged(); return; }
            if (SetProperty(ref _selectedDetail, IntelligenceDetail.Normalize(value))) QueueChatSave();
        }
    }

    public ObservableCollection<IntelligenceChatItemViewModel> Chats { get; } = new();
    public FilteredView GroupedChats { get; private set; } = null!;
    public RelayCommand RenameChatCommand { get; private set; } = null!;
    public RelayCommand SaveChatNameCommand { get; private set; } = null!;
    public RelayCommand CancelChatNameCommand { get; private set; } = null!;
    public RelayCommand DeleteChatCommand { get; private set; } = null!;
    public RelayCommand PinChatCommand { get; private set; } = null!;
    public bool CanManageChats => !IsBusy && ShowConversation && _chatScope is not null;

    public bool IsRenamingChat
    {
        get => _isRenamingChat;
        private set
        {
            SetProperty(ref _isRenamingChat, value);
            foreach (var chat in Chats) chat.IsRenaming = value && ReferenceEquals(chat, SelectedChat);
        }
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
        // Grouped by ActivityGroup in the view (#249), live, as chats move between groups.
        GroupedChats = new FilteredView(Chats);
        _chatGroupingTimer.Tick += (_, _) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var chat in Chats) chat.RefreshActivityGroup(today);
        };
        _chatGroupingTimer.Start();
        _chatStore = store;
        _chatWriter = new IntelligenceChatWriter(store, _logger);
        _chatWriter.Failed += message => _dispatcher.InvokeAsync(() => ErrorText ??= message);
        RenameChatCommand = new RelayCommand(parameter =>
        {
            if (parameter is IntelligenceChatItemViewModel target)
            {
                if (!Chats.Contains(target)) return;
                SelectedChat = target;
            }
            ChatNameDraft = SelectedChat?.Title ?? string.Empty;
            IsRenamingChat = true;
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
            if (!_chatStorageReadOnly) _chatWriter.Save(_chatScope, Snapshot(chat.Data), makeActive: false);
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

        // Read back only once every save queued so far is on disk.
        _chatWriter.Flush();
        _changingChat = true;
        _selectedChat = null;
        Chats.Clear();
        _chatScope = scope;
        _chatStorageReadOnly = false;
        Guid? activeId = null;
        var unreadable = 0;
        try
        {
            if (scope is not null)
            {
                var saved = _chatStore.Load(scope);
                activeId = saved.ActiveId;
                foreach (var chat in saved.Chats) Chats.Add(new(chat));
                foreach (var file in saved.Unreadable ?? Array.Empty<string>())
                {
                    _logger.LogWarning("Intelligence: saved chat {File} could not be read and was skipped", file);
                }

                unreadable = saved.Unreadable?.Count ?? 0;
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

        // After selecting, which clears the error line (#322).
        if (unreadable > 0)
        {
            ErrorText = unreadable == 1
                ? "One saved chat couldn't be opened and was skipped. Your other chats are here."
                : $"{unreadable} saved chats couldn't be opened and were skipped. Your other chats are here.";
        }

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
            ReplyStyle = IntelligenceDetail.Normalize(_settings.Current.IntelligenceReplyStyle),
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
                foreach (var message in chat.Messages)
                {
                    var restored = IntelligenceMessageViewModel.Restore(message);
                    restored.SeedInlineReferences(Messages.Where(item => item.IsAssistant).Reverse().SelectMany(item => item.InlineReferences));
                    Messages.Add(restored);
                }
            Question = chat?.Draft ?? string.Empty;
            SelectedDetail = IntelligenceDetail.Normalize(chat?.ReplyStyle ?? _settings.Current.IntelligenceReplyStyle);
            CompanionOutfit = chat?.Outfit ?? DashyOutfit.Normal;
            var model = chat?.Model ?? _settings.Current.IntelligenceModel;
            SelectedModel = Models.FirstOrDefault(item => item.Slug == model)
                ?? (model is not null ? new ChatGptModel(model, ChatGptModel.NameFor(model)) : Models.FirstOrDefault());
            Context.RestoreChatContext(chat?.Spotlight ?? new(), chat?.LookedAt ?? new(),
                Messages.Where(m => m.IsAssistant).Select(m => IntelligenceInlineMarkup.ToReadableText(m.Text, m.InlineReferences)));
            IsRenamingChat = false;
            if (!_chatStorageReadOnly) ErrorText = null;
            OnPropertyChanged(nameof(ShowSuggestions));
            RaiseCommands();
        }
        finally { _changingChat = false; }
    }

    /// <summary>Saves the chat shortly, once changes settle - restarting the wait on each one (#321).</summary>
    private void QueueChatSave()
    {
        if (_changingChat || _chatStore is null) return;
        _chatSaveTimer.Stop();
        _chatSaveTimer.Start();
    }

    /// <summary>While an answer streams: no save per chunk, just one now and then in case the app is closed mid-answer (#321).</summary>
    private void SaveWhileStreaming()
    {
        if (DateTimeOffset.UtcNow - _lastChatSave >= StreamingSaveInterval) SaveActiveChat();
    }

    private void SaveActiveChat(bool recordActivity = false)
    {
        _chatSaveTimer.Stop();
        if (_changingChat || _chatStore is null || _chatScope is null || SelectedChat is not { } item) return;
        var chat = item.Data;
        chat.Draft = Question;
        chat.Model = SelectedModel?.Slug ?? chat.Model;
        chat.ReplyStyle = SelectedDetail;
        chat.Outfit = CompanionOutfit;
        chat.History = _conversation.ExportHistory();
        chat.Messages = Messages.Select(message => message.Save()).ToList();
        chat.Spotlight = Context.Spotlight.Select(option => new SavedChatSpotlight((int)option.Kind, option.Id, option.Name)).ToList();
        chat.LookedAt = Context.LookedAt.ToList();
        // Draft saves, selection and renaming do not change conversation activity.
        if (recordActivity) chat.UpdatedAt = DateTimeOffset.UtcNow;
        item.Refresh();
        _lastChatSave = DateTimeOffset.UtcNow;
        if (_chatStorageReadOnly) return;

        // Serialised, encrypted and written off the UI thread (#321).
        _chatWriter.Save(_chatScope, Snapshot(chat));
        SortChats();
    }

    /// <summary>
    /// A copy for the writer to serialise while the UI thread carries on. Each
    /// save builds fresh message, history and context lists, so only the
    /// chat's own fields need copying.
    /// </summary>
    private static SavedIntelligenceChat Snapshot(SavedIntelligenceChat chat) => new()
    {
        Id = chat.Id,
        Title = chat.Title,
        IsRenamed = chat.IsRenamed,
        IsPinned = chat.IsPinned,
        UpdatedAt = chat.UpdatedAt,
        Model = chat.Model,
        ReplyStyle = chat.ReplyStyle,
        Outfit = chat.Outfit,
        Draft = chat.Draft,
        History = chat.History,
        Messages = chat.Messages.ToList(),
        Spotlight = chat.Spotlight.ToList(),
        LookedAt = chat.LookedAt.ToList(),
    };

    /// <summary>Writes any chat saves still waiting - the app is closing.</summary>
    public void Dispose()
    {
        SaveActiveChat();
        _chatWriter?.Dispose();
    }

    private void DeleteChat(object? parameter)
    {
        var chat = parameter as IntelligenceChatItemViewModel ?? SelectedChat;
        if (chat is null || _chatScope is null || !Chats.Contains(chat)) return;
        var scope = _chatScope;
        if (!_windows.Confirm("Delete chat?", $"Delete '{chat.Title}' and its saved messages from this PC?", "Delete", destructive: true)) return;
        if (_chatScope != scope || !Chats.Contains(chat)) return;
        if (!_chatStorageReadOnly) _chatWriter.Delete(_chatScope, chat.Data.Id);
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
    private IntelligenceChatActivityGroup _activityGroup = IntelligenceChatActivityGroups.ForDate(
        DateOnly.FromDateTime(data.UpdatedAt.ToLocalTime().DateTime), DateOnly.FromDateTime(DateTime.Now), data.IsPinned);
    public IntelligenceChatActivityGroup ActivityGroup => _activityGroup;
    public void RefreshActivityGroup(DateOnly today) => SetProperty(ref _activityGroup,
        IntelligenceChatActivityGroups.ForDate(DateOnly.FromDateTime(Data.UpdatedAt.ToLocalTime().DateTime), today, IsPinned), nameof(ActivityGroup));
    private bool _isRenaming;
    public bool IsRenaming { get => _isRenaming; set => SetProperty(ref _isRenaming, value); }
    public string LastActivity => Data.Messages.Count == 0 ? string.Empty
        : Data.UpdatedAt.ToLocalTime().ToString("g");
    public string Preview
    {
        get
        {
            var message = Data.Messages.LastOrDefault(message => !string.IsNullOrWhiteSpace(message.Text));
            if (message is null) return "Start a conversation";
            var references = message.StatusCards.SelectMany(IntelligenceInlineReferences.FromSnapshot)
                .Concat(message.PriorReferences).DistinctBy(reference => reference.Key).ToArray();
            return IntelligenceInlineMarkup.ToPreviewText(message.Text, references);
        }
    }
    public void Refresh()
    {
        RefreshActivityGroup(DateOnly.FromDateTime(DateTime.Now));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinLabel));
        OnPropertyChanged(nameof(LastActivity));
    }
}

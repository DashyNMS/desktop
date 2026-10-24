using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.Services.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// What the chat list needs from the conversation it belongs to (#327): whether
/// an answer is running, which account and network the chats are for, and how
/// to write the open conversation into a chat or show a chat again.
/// </summary>
internal interface IIntelligenceChatHost
{
    bool IsBusy { get; }

    bool ShowConversation { get; }

    bool SingleChatMode { get; }

    /// <summary>The ChatGPT account and LibreNMS server the chats belong to, or null when either is missing.</summary>
    string? ChatScope { get; }

    string? ErrorText { get; set; }

    void CancelAnswer();

    /// <summary>Writes the open conversation - draft, model, messages, context - into <paramref name="chat"/>.</summary>
    void Capture(SavedIntelligenceChat chat);

    /// <summary>Shows <paramref name="chat"/>, or an empty conversation for null.</summary>
    void Restore(SavedIntelligenceChat? chat);

    /// <summary>A new, empty chat with the current defaults.</summary>
    SavedIntelligenceChat NewChat();
}

/// <summary>
/// The Intelligence chat list (#327): saved chats for this account and network,
/// grouped by when they were last used, with pin, rename and delete, and their
/// persistence - saved shortly after a change and written off the UI thread.
/// </summary>
public sealed class IntelligenceChatListViewModel : ObservableObject, IDisposable
{
    /// <summary>How long after the last change - typing above all - a chat is saved (#321).</summary>
    private static readonly TimeSpan ChatSaveDelay = TimeSpan.FromSeconds(3);

    /// <summary>While an answer streams, saved at most this often, so a crash loses little (#321).</summary>
    private static readonly TimeSpan StreamingSaveInterval = TimeSpan.FromSeconds(20);

    private readonly IIntelligenceChatHost _host;
    private readonly IIntelligenceChatStore _store;
    private readonly IntelligenceChatWriter _writer;
    private readonly IWindowService _windows;
    private readonly ILogger _logger;
    private readonly IUiTimer _saveTimer;
    private readonly IUiTimer _groupingTimer = UiThread.CreateTimer(TimeSpan.FromMinutes(1));
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private string? _scope;
    private bool _singleChatMode;
    private IntelligenceChatItemViewModel? _selectedChat;
    private bool _isRenamingChat;
    private string _chatNameDraft = string.Empty;

    internal IntelligenceChatListViewModel(IIntelligenceChatHost host, IIntelligenceChatStore store, IWindowService windows, IUiDispatcher dispatcher, ILogger logger, IUiTimer? saveTimer = null)
    {
        _host = host;
        _store = store;
        _windows = windows;
        _logger = logger;
        _saveTimer = saveTimer ?? UiThread.CreateTimer(ChatSaveDelay);

        // Grouped by ActivityGroup in the view (#249), live, as chats move between groups.
        GroupedChats = new FilteredView(Chats);
        _groupingTimer.Tick += (_, _) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var chat in Chats) chat.RefreshActivityGroup(today);
        };
        _groupingTimer.Start();
        _writer = new IntelligenceChatWriter(store, logger);
        _writer.Failed += message => dispatcher.InvokeAsync(() => _host.ErrorText ??= message);
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
            SaveActive();
        }, () => CanManageChats && !string.IsNullOrWhiteSpace(ChatNameDraft));
        CancelChatNameCommand = new RelayCommand(() => IsRenamingChat = false);
        DeleteChatCommand = new RelayCommand(DeleteChat, _ => CanManageChats && SelectedChat is not null);
        PinChatCommand = new RelayCommand(parameter =>
        {
            if (!CanManageChats || parameter is not IntelligenceChatItemViewModel chat || _scope is null || !Chats.Contains(chat)) return;
            chat.Data.IsPinned = !chat.Data.IsPinned;
            chat.Refresh();
            if (!IsReadOnly) _writer.Save(_scope, Snapshot(chat.Data), makeActive: false);
            Sort();
        }, _ => CanManageChats);
        _saveTimer.Tick += (_, _) => SaveActive();
    }

    public ObservableCollection<IntelligenceChatItemViewModel> Chats { get; } = new();

    public FilteredView GroupedChats { get; }

    public RelayCommand RenameChatCommand { get; }

    public RelayCommand SaveChatNameCommand { get; }

    public RelayCommand CancelChatNameCommand { get; }

    public RelayCommand DeleteChatCommand { get; }

    public RelayCommand PinChatCommand { get; }

    /// <summary>The list's "New conversation" button - the conversation decides what a new one starts with.</summary>
    public RelayCommand NewChatCommand { get; internal set; } = null!;

    public bool IsBusy => _host.IsBusy;

    public bool IsSingleChatMode => _host.SingleChatMode;

    public bool CanEditConversation => !_host.IsBusy && _host.ShowConversation && _scope is not null
        && _singleChatMode == _host.SingleChatMode;

    public bool CanManageChats => CanEditConversation && !IsSingleChatMode;

    /// <summary>A chat is being shown or the list reloaded - changes made meanwhile aren't edits to save.</summary>
    public bool IsChangingChat { get; private set; }

    /// <summary>The saved chats couldn't be read, so nothing is written over them.</summary>
    public bool IsReadOnly { get; private set; }

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
        set { if (SetProperty(ref _chatNameDraft, value)) SaveChatNameCommand.RaiseCanExecuteChanged(); }
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
            if (_host.IsBusy) { OnPropertyChanged(); return; }
            SaveActive();
            _selectedChat = value;
            OnPropertyChanged();
            Show(value?.Data);
            SaveActive();
        }
    }

    /// <summary>
    /// Loads the chats for the current account and network, when they've
    /// changed or none is open - after sign-in, a server change, or an answer.
    /// </summary>
    public void EnsureScope()
    {
        var scope = _host.ChatScope;
        if (scope == _scope && _singleChatMode == _host.SingleChatMode && (scope is null || SelectedChat is not null)) return;
        _saveTimer.Stop();
        if (_host.IsBusy) { _host.CancelAnswer(); return; }
        SaveActive();

        // Read back only once every save queued so far is on disk.
        _writer.Flush();
        IsChangingChat = true;
        _selectedChat = null;
        Chats.Clear();
        _scope = scope;
        _singleChatMode = _host.SingleChatMode;
        IsReadOnly = false;
        Guid? activeId = null;
        var unreadable = 0;
        try
        {
            if (scope is not null && !_singleChatMode)
            {
                var saved = _store.Load(scope);
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
            IsReadOnly = true;
            _logger.LogWarning(ex, "Intelligence: saved chats could not be read");
            _host.ErrorText = "Couldn't read saved chats. The existing files have been kept; this chat won't be saved.";
        }
        finally { IsChangingChat = false; }
        if (scope is null)
        {
            Show(null);
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
            _host.ErrorText = unreadable == 1
                ? "One saved chat couldn't be opened and was skipped. Your other chats are here."
                : $"{unreadable} saved chats couldn't be opened and were skipped. Your other chats are here.";
        }

        RaiseCommands();
    }

    /// <summary>Opens a new chat - or the blank one already in the list, rather than adding another.</summary>
    public void NewChat(bool force = false)
    {
        if (_host.IsBusy || _scope is null) return;
        SaveActive();
        if (_singleChatMode)
        {
            _selectedChat = null;
            Chats.Clear();
        }
        var draft = force ? null : Chats.FirstOrDefault(chat => chat.Data.Messages.Count == 0
            && string.IsNullOrWhiteSpace(chat.Data.Draft) && !chat.Data.IsRenamed);
        if (draft is not null)
        {
            if (draft != SelectedChat) SelectedChat = draft;
            return;
        }
        var chat = new IntelligenceChatItemViewModel(_host.NewChat());
        Chats.Insert(0, chat);
        SelectedChat = chat;
        Sort();
    }

    /// <summary>Names a chat from its first question, unless it's been renamed.</summary>
    public void NameFromQuestion(string question)
    {
        if (SelectedChat is { } chat && !chat.Data.IsRenamed && chat.Data.Messages.Count == 0)
        {
            chat.Data.Title = ChatTitle(question);
            chat.Refresh();
        }
    }

    /// <summary>Saves the chat shortly, once changes settle - restarting the wait on each one (#321).</summary>
    public void QueueSave()
    {
        if (IsChangingChat || _singleChatMode || _host.SingleChatMode) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>While an answer streams: no save per chunk, just one now and then in case the app is closed mid-answer (#321).</summary>
    public void SaveWhileStreaming()
    {
        if (DateTimeOffset.UtcNow - _lastSave >= StreamingSaveInterval) SaveActive();
    }

    /// <summary>Saves the open chat now; <paramref name="recordActivity"/> when the conversation itself moved on.</summary>
    public void SaveActive(bool recordActivity = false)
    {
        _saveTimer.Stop();
        if (IsChangingChat || _singleChatMode || _host.SingleChatMode || _scope is null || SelectedChat is not { } item) return;
        var chat = item.Data;
        _host.Capture(chat);

        // Draft saves, selection and renaming do not change conversation activity.
        if (recordActivity) chat.UpdatedAt = DateTimeOffset.UtcNow;
        item.Refresh();
        _lastSave = DateTimeOffset.UtcNow;
        if (IsReadOnly) return;

        // Serialised, encrypted and written off the UI thread (#321).
        _writer.Save(_scope, Snapshot(chat));
        Sort();
    }

    public void RaiseCommands()
    {
        OnPropertyChanged(nameof(IsSingleChatMode));
        OnPropertyChanged(nameof(CanEditConversation));
        OnPropertyChanged(nameof(CanManageChats));
        OnPropertyChanged(nameof(IsBusy));
        RenameChatCommand.RaiseCanExecuteChanged();
        SaveChatNameCommand.RaiseCanExecuteChanged();
        DeleteChatCommand.RaiseCanExecuteChanged();
        PinChatCommand.RaiseCanExecuteChanged();
        NewChatCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Writes any chat saves still waiting - the app is closing.</summary>
    public void Dispose()
    {
        SaveActive();
        _groupingTimer.Stop();
        _writer.Dispose();
    }

    private void Show(SavedIntelligenceChat? chat)
    {
        IsChangingChat = true;
        try
        {
            _host.Restore(chat);
            IsRenamingChat = false;
        }
        finally { IsChangingChat = false; }
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

    private void DeleteChat(object? parameter)
    {
        if (!CanManageChats) return;
        var chat = parameter as IntelligenceChatItemViewModel ?? SelectedChat;
        if (chat is null || _scope is null || !Chats.Contains(chat)) return;
        var scope = _scope;
        if (!_windows.Confirm("Delete chat?", $"Delete '{chat.Title}' and its saved messages from this PC?", "Delete", destructive: true)) return;
        if (_scope != scope || !Chats.Contains(chat)) return;
        if (!IsReadOnly) _writer.Delete(_scope, chat.Data.Id);
        var wasSelected = ReferenceEquals(chat, SelectedChat);
        if (wasSelected) _selectedChat = null;
        Chats.Remove(chat);
        if (wasSelected)
        {
            if (Chats.Count > 0) SelectedChat = Chats[0];
            else NewChat();
        }
    }

    private void Sort()
    {
        var sorted = Chats.OrderByDescending(chat => chat.IsPinned).ThenByDescending(chat => chat.Data.UpdatedAt).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var index = Chats.IndexOf(sorted[i]);
            if (index != i) Chats.Move(index, i);
        }
    }

    private static string ChatTitle(string value)
    {
        var title = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return title.Length > 80 ? title[..77] + "…" : title;
    }
}

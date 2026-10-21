using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The Intelligence composer (#327): the question being written, the model and
/// detail level for this chat, and Ask and Stop. Asking itself is the
/// conversation's job; this holds what the next question goes out with.
/// </summary>
public sealed class IntelligenceComposerViewModel : ObservableObject
{
    private readonly Func<bool> _isLocked;
    private string _question = string.Empty;
    private ChatGptModel? _selectedModel;
    private string _selectedDetail = IntelligenceDetail.Default;
    private bool _isBusy;
    private bool _canChangeChat;

    /// <param name="models">The models the plan offers - shared with Settings.</param>
    /// <param name="isLocked">True while an answer runs, when the detail level can't change.</param>
    internal IntelligenceComposerViewModel(ObservableCollection<ChatGptModel> models, AsyncRelayCommand send, RelayCommand stop, Func<bool> isLocked)
    {
        Models = models;
        SendCommand = send;
        StopCommand = stop;
        _isLocked = isLocked;
    }

    /// <summary>Something the chat saves changed: the draft, the model or the detail level.</summary>
    public event EventHandler? Edited;

    public ObservableCollection<ChatGptModel> Models { get; }

    public IReadOnlyList<KeyValuePair<string, string>> DetailOptions => IntelligenceDetailOptions.All;

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand StopCommand { get; }

    public string Question
    {
        get => _question;
        set
        {
            if (SetProperty(ref _question, value ?? string.Empty))
            {
                SendCommand.RaiseCanExecuteChanged();
                Edited?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public ChatGptModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                Edited?.Invoke(this, EventArgs.Empty);
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>How much this chat's next answer says (#329) - saved with the chat as its ReplyStyle.</summary>
    public string SelectedDetail
    {
        get => _selectedDetail;
        set
        {
            if (value is null) return;
            if (_isLocked()) { OnPropertyChanged(); return; }
            if (SetProperty(ref _selectedDetail, IntelligenceDetail.Normalize(value))) Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>An answer is running: Stop shows instead of Ask.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        internal set => SetProperty(ref _isBusy, value);
    }

    /// <summary>The model and detail level can change - not mid-answer, or without a chat.</summary>
    public bool CanChangeChat
    {
        get => _canChangeChat;
        internal set => SetProperty(ref _canChangeChat, value);
    }
}

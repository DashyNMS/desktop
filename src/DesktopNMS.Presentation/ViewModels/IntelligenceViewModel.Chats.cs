using System;
using System.Linq;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.ViewModels;

/// <summary>The conversation's side of the chat list (#327): what a saved chat holds, and showing one again.</summary>
public sealed partial class IntelligenceViewModel : IIntelligenceChatHost
{
    string? IIntelligenceChatHost.ChatScope => _credentials?.Subject is { Length: > 0 } account
        && _session.Connection is { } connection
        ? account + "\n" + connection.WebRoot.AbsoluteUri.TrimEnd('/')
        : null;

    string? IIntelligenceChatHost.ErrorText
    {
        get => ErrorText;
        set => ErrorText = value;
    }

    void IIntelligenceChatHost.CancelAnswer() => _running?.Cancel();

    void IIntelligenceChatHost.Capture(SavedIntelligenceChat chat)
    {
        chat.Draft = Composer.Question;
        chat.Model = Composer.SelectedModel?.Slug ?? chat.Model;
        chat.ReplyStyle = Composer.SelectedDetail;
        chat.Outfit = CompanionOutfit;
        chat.History = _conversation.ExportHistory();
        chat.Messages = Messages.Select(message => message.Save()).ToList();
        chat.Spotlight = Context.Spotlight.Select(option => new SavedChatSpotlight((int)option.Kind, option.Id, option.Name) { Object = option.Object }).ToList();
        chat.LookedAt = Context.LookedAt.ToList();
    }

    void IIntelligenceChatHost.Restore(SavedIntelligenceChat? chat)
    {
        _conversation.RestoreHistory(chat?.History ?? new());
        Messages.Clear();
        if (chat is not null)
            foreach (var message in chat.Messages)
            {
                var restored = IntelligenceMessageViewModel.Restore(message);
                restored.SeedInlineReferences(Messages.Where(item => item.IsAssistant).Reverse().SelectMany(item => item.InlineReferences));
                Messages.Add(restored);
            }
        Composer.Question = chat?.Draft ?? string.Empty;
        Composer.SelectedDetail = IntelligenceDetail.Normalize(chat?.ReplyStyle ?? _settings.Current.IntelligenceReplyStyle);
        CompanionOutfit = chat?.Outfit ?? DashyOutfit.Normal;
        var model = chat?.Model ?? _settings.Current.IntelligenceModel;
        Composer.SelectedModel = Models.FirstOrDefault(item => item.Slug == model)
            ?? (model is not null ? new ChatGptModel(model, ChatGptModel.NameFor(model)) : Models.FirstOrDefault());
        Context.RestoreChatContext(chat?.Spotlight ?? new(), chat?.LookedAt ?? new(),
            Messages.Where(m => m.IsAssistant).Select(m => IntelligenceInlineMarkup.ToReadableText(m.Text, m.InlineReferences)));
        if (!Chats.IsReadOnly) ErrorText = null;
        RotateSuggestions();
        OnPropertyChanged(nameof(ShowSuggestions));
        RaiseCommands();
    }

    SavedIntelligenceChat IIntelligenceChatHost.NewChat() => new()
    {
        Model = _settings.Current.IntelligenceModel ?? Composer.SelectedModel?.Slug,
        ReplyStyle = IntelligenceDetail.Normalize(_settings.Current.IntelligenceReplyStyle),
        Outfit = DashyOutfits.Pick(Random.Shared.Next(100)),
    };
}

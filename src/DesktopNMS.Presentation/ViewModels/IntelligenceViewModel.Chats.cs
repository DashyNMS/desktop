using System;
using System.Collections.Generic;
using System.Linq;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.ViewModels;

/// <summary>The conversation's side of the chat list (#327): what a saved chat holds, and showing one again.</summary>
public sealed partial class IntelligenceViewModel : IIntelligenceChatHost
{
    bool IIntelligenceChatHost.SingleChatMode => _settings.Current.IntelligenceSingleChatMode;

    /// <summary>Where a new chat is saved: under the provider it starts with.</summary>
    string? IIntelligenceChatHost.ChatScope => ScopeFor(DefaultModel(null)?.Provider ?? _chatProvider);

    IReadOnlyDictionary<string, string> IIntelligenceChatHost.ChatScopes => Providers
        .Select(p => (p.Id, Scope: ScopeFor(p.Id)))
        .Where(p => p.Scope is not null)
        .ToDictionary(p => p.Id, p => p.Scope!, StringComparer.Ordinal);

    string? IIntelligenceChatHost.ScopeForOpenChat => ScopeFor(_chatProvider);

    /// <summary>
    /// The account and server a provider's chats are saved under, or null when
    /// it can't answer or there's no network. ChatGPT's is unchanged from before
    /// providers existed, so its saved chats are where they always were; an
    /// agent's is prefixed with the provider, and its account is a digest.
    /// </summary>
    private string? ScopeFor(string provider)
    {
        if (_session.Connection is not { } connection) return null;
        var server = connection.WebRoot.AbsoluteUri.TrimEnd('/');
        if (provider == AppSettings.IntelligenceProviderChatGpt)
        {
            return _credentials?.Subject is { Length: > 0 } account ? account + "\n" + server : null;
        }

        return IsReady(provider) && Providers.FirstOrDefault(p => p.Id == provider) is { Status.AccountKey: { Length: > 0 } key }
            ? provider + ":" + key + "\n" + server
            : null;
    }

    string? IIntelligenceChatHost.ErrorText
    {
        get => ErrorText;
        set => ErrorText = value;
    }

    void IIntelligenceChatHost.CancelAnswer() => _running?.Cancel();

    void IIntelligenceChatHost.Capture(SavedIntelligenceChat chat)
    {
        chat.Draft = Composer.Question;
        chat.Provider = _chatProvider;
        chat.Model = Composer.SelectedModel?.Id ?? chat.Model;
        chat.Effort = Composer.SelectedEffort;
        chat.ProviderSession = _providerSession;
        chat.ReplyStyle = Composer.SelectedDetail;
        chat.Outfit = CompanionOutfit;
        chat.History = _conversation.ExportHistory();
        chat.Messages = Messages.Select(message => message.Save()).ToList();
        chat.Spotlight = Context.Spotlight.Select(option => new SavedChatSpotlight((int)option.Kind, option.Id, option.Name) { Object = option.Object }).ToList();
        chat.LookedAt = Context.LookedAt.ToList();
    }

    void IIntelligenceChatHost.Restore(SavedIntelligenceChat? chat)
    {
        _chatProvider = chat?.Provider ?? (chat is null ? DefaultModel(null)?.Provider : null) ?? AppSettings.IntelligenceProviderChatGpt;
        _providerSession = chat?.ProviderSession;

        // Only ChatGPT's own chats carry its history; a moved chat starts without it.
        _conversation.RestoreHistory(_chatProvider == AppSettings.IntelligenceProviderChatGpt ? chat?.History ?? new() : new());
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
        var provider = Providers.FirstOrDefault(p => p.Id == _chatProvider);
        var model = chat?.Model;
        Composer.Restore(
            provider?.Models.FirstOrDefault(item => item.Id == model)
            ?? (model is not null ? IntelligenceModelOption.Unlisted(_chatProvider, model) : DefaultModel(_chatProvider)),
            chat is null ? DefaultEffort(_chatProvider) : chat.Effort);
        OnPropertyChanged(nameof(ChatProvider));
        Context.RestoreChatContext(chat?.Spotlight ?? new(), chat?.LookedAt ?? new(),
            Messages.Where(m => m.IsAssistant).Select(m => IntelligenceInlineMarkup.ToReadableText(m.Text, m.InlineReferences)));
        if (!Chats.IsReadOnly) ErrorText = null;
        RotateSuggestions();
        OnPropertyChanged(nameof(ShowSuggestions));
        RaiseCommands();
    }

    /// <summary>A new chat starts on the default provider - or the first that can answer - and its default model.</summary>
    SavedIntelligenceChat IIntelligenceChatHost.NewChat()
    {
        var model = DefaultModel(null);
        var provider = model?.Provider ?? AppSettings.IntelligenceProviderChatGpt;
        return new()
        {
            Provider = provider,
            Model = model?.Id ?? (provider == AppSettings.IntelligenceProviderChatGpt ? _settings.Current.IntelligenceModel : null),
            Effort = DefaultEffort(provider),
            ReplyStyle = IntelligenceDetail.Normalize(_settings.Current.IntelligenceReplyStyle),
            Outfit = DashyOutfits.Pick(Random.Shared.Next(100)),
        };
    }
}

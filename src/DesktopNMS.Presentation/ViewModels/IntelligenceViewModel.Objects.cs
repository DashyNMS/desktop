using DesktopNMS.Core.Api;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.ViewModels;

public sealed partial class IntelligenceViewModel
{
    private bool _isOpeningObject;
    public string? ObjectServer => _session.Connection?.WebRoot.AbsoluteUri.TrimEnd('/');
    public string? ObjectChatScope => ((IIntelligenceChatHost)this).ChatScope;
    public bool CanStartObjectChat => _settings.Current.IntelligenceEnabled && !IsBusy && !_isOpeningObject;
    public bool ShowObjectActions => _settings.Current.IntelligenceEnabled;

    public IntelligenceChatRequest? ObjectRequest(object? value, string? sourceServer)
        => sourceServer is not null && IntelligenceObjectTargets.From(value) is { } target
            ? new(target, sourceServer, ObjectChatScope) : null;

    private bool MatchesCurrentScope(IntelligenceChatRequest request) => request.Matches(ObjectServer, ObjectChatScope);

    public async Task StartObjectChatAsync(IntelligenceChatRequest request)
    {
        if (!CanStartObjectChat) return;
        if (!IsSignedIn) { ErrorText = "Sign in with ChatGPT, then choose Ask Intelligence again."; return; }
        if (!MatchesCurrentScope(request)) { ErrorText = "This object belongs to a different server or sign-in. Open it again on the current network."; return; }
        SetOpeningObject(true);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var target = await Context.ResolveObjectAsync(request.Target, cancel.Token);
            if (!MatchesCurrentScope(request) || !_settings.Current.IntelligenceEnabled || IsBusy) return;
            if (target is null) { ErrorText = "This object is no longer available to this account. Your current chat has been kept."; return; }
            Chats.EnsureScope();
            Chats.NewChat(force: true);
            Context.AddSpotlight(target, replace: true);
            Chats.SaveActive();
            ErrorText = null;
            QuestionFocusRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is LibreNmsApiException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            ErrorText = "Couldn't check access to this object. Your current chat has been kept; try again when the network is available.";
        }
        finally { SetOpeningObject(false); }
    }

    private void SetOpeningObject(bool value)
    {
        _isOpeningObject = value;
        OnPropertyChanged(nameof(CanStartObjectChat));
    }

    public async Task SpotlightReferenceAsync(IntelligenceMessageViewModel message, IntelligenceInlineReference reference, bool replace)
    {
        if (!CanStartObjectChat || !Messages.Contains(message) || message.SpotlightTarget(reference) is not { } candidate
            || ObjectServer is not { } server || ObjectChatScope is null) return;
        var request = new IntelligenceChatRequest(candidate, server, ObjectChatScope);
        var chat = Chats.SelectedChat;
        SetOpeningObject(true);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var target = await Context.ResolveObjectAsync(candidate, cancel.Token);
            if (!MatchesCurrentScope(request) || Chats.SelectedChat != chat || !Messages.Contains(message)
                || IsBusy || !_settings.Current.IntelligenceEnabled) return;
            if (target is null) { ErrorText = "This object is no longer available to this account. Your Spotlight has been kept."; return; }
            if (!Context.AddSpotlight(target, replace)) { ErrorText = "Spotlight is full. Remove an object or choose Spotlight only this object."; return; }
            Chats.SaveActive();
            IsSidebarOpen = true;
            ErrorText = null;
            QuestionFocusRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is LibreNmsApiException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            ErrorText = "Couldn't check access to this object. Your Spotlight has been kept; try again when the network is available.";
        }
        finally { SetOpeningObject(false); }
    }
}

public sealed record IntelligenceReferenceAction(IntelligenceInlineReference Reference, bool Replace);

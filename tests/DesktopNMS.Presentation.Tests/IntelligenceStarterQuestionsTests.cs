using DesktopNMS.Services.Intelligence;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class IntelligenceStarterQuestionsTests
{
    [Fact]
    public void Drafts_and_existing_conversations_keep_their_content_and_reject_stale_suggestions()
    {
        var credentials = new ChatGptCredentials();
        var settings = Proxy<ISettingsStore>((_, _) => new AppSettings());
        var devices = Proxy<IDeviceCache>((method, _) => method.Name == "get_All" ? Array.Empty<Device>() : null);
        var client = Proxy<ILibreNmsClient>((_, _) => null);
        using var model = new IntelligenceViewModel(Proxy<IChatGptCredentialStore>((_, _) => credentials), null!,
            Proxy<ISessionService>((_, _) => null), Proxy<IWindowService>((_, _) => null), devices, client, settings,
            new AlertMonitor(client, null!, settings, null!, devices, NullLogger<AlertMonitor>.Instance),
            Proxy<IIntelligenceChatStore>((_, _) => null), NullLogger<IntelligenceViewModel>.Instance);
        credentials.AccessToken = "synthetic";
        credentials.RefreshToken = "synthetic";
        model.ConsentGiven = true;
        var host = (IIntelligenceChatHost)model;
        host.Restore(new());
        model.Composer.SelectedModel = new ChatGptModel("example", "Example");
        Assert.True(model.ShowSuggestions);
        var suggestion = model.Suggestions[0];
        Assert.True(model.AskSuggestionCommand.CanExecute(suggestion));
        var modelChanges = 0;
        model.AskSuggestionCommand.CanExecuteChanged += (_, _) => modelChanges++;
        model.Composer.SelectedModel = null;
        Assert.False(model.AskSuggestionCommand.CanExecute(suggestion));
        model.Composer.SelectedModel = new ChatGptModel("example", "Example");
        Assert.Equal(2, modelChanges);
        Assert.True(model.AskSuggestionCommand.CanExecute(suggestion));

        var original = model.Suggestions.ToArray();
        host.Restore(new() { Draft = "Keep my unfinished question" });
        Assert.False(model.ShowSuggestions);
        Assert.False(model.AskSuggestionCommand.CanExecute(suggestion));
        model.AskSuggestionCommand.Execute(suggestion);
        Assert.Equal("Keep my unfinished question", model.Composer.Question);
        Assert.Equal(original, model.Suggestions);

        host.Restore(new() { Messages = [new() { IsUser = true, Text = "Previous question" }] });
        Assert.False(model.ShowSuggestions);
        Assert.False(model.AskSuggestionCommand.CanExecute(suggestion));
        model.AskSuggestionCommand.Execute(suggestion);
        Assert.Equal("Previous question", Assert.Single(model.Messages).Text);
        Assert.Equal(string.Empty, model.Composer.Question);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Each_new_chat_offers_four_distinct_questions_and_a_different_selection(bool devices, bool alerts)
    {
        var questions = new IntelligenceStarterQuestions();
        var previous = questions.Next(devices, alerts);
        for (var chat = 0; chat < 24; chat++)
        {
            var current = questions.Next(devices, alerts);
            Assert.Equal(4, current.Count);
            Assert.Equal(4, current.Distinct().Count());
            Assert.False(current.SequenceEqual(previous));
            previous = current;
        }
    }

    [Fact]
    public void Missing_device_and_alert_context_does_not_offer_specific_device_or_open_alert_prompts()
    {
        var questions = new IntelligenceStarterQuestions();
        var offered = Enumerable.Range(0, 6).SelectMany(_ => questions.Next(false, false)).ToHashSet();
        Assert.DoesNotContain("Which devices are currently unreachable?", offered);
        Assert.DoesNotContain("What's alerting right now?", offered);
        Assert.Contains("How is the network doing?", offered);
        Assert.Contains("Are any links close to capacity?", offered);
    }

    [Fact]
    public void Cached_devices_and_open_alerts_add_relevant_questions_without_mutating_previous_choices()
    {
        var questions = new IntelligenceStarterQuestions();
        var initial = questions.Next(false, false);
        var snapshot = initial.ToArray();
        var offered = Enumerable.Range(0, 6).SelectMany(_ => questions.Next(true, true)).ToHashSet();
        Assert.Contains("Which devices are currently unreachable?", offered);
        Assert.Contains("What's alerting right now?", offered);
        Assert.Equal(snapshot, initial);
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Handler = handler;
        return proxy;
    }
}

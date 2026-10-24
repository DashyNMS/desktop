using System.Net.Http;
using DesktopNMS.Core.SignIn;
using DesktopNMS.Services;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class WebSignInSessionTests
{
    private static WebTokenSignIn Flow() => new(new Uri("https://nms.example.net/"), "DashyNMS example");

    [Fact]
    public async Task An_account_without_the_creation_form_returns_the_manual_token_fallback()
    {
        var flow = Flow(); WebSignInResult? result = null;
        using var session = new WebSignInSession(flow, () => flow.TokensPage,
            _ => Task.FromResult<string?>("signed-in"), _ => { }, value => result = value, TimeSpan.Zero);
        await session.PageLoadedAsync();
        Assert.Equal(WebSignInOutcome.NotAllowed, result!.Outcome);
    }

    [Fact]
    public async Task Only_the_server_is_probed_and_a_token_is_returned_without_an_extra_submit()
    {
        var flow = Flow(); var uri = new Uri("https://sso.example.net/login");
        var scripts = new List<string>(); WebSignInResult? result = null;
        using var session = new WebSignInSession(flow, () => uri, text =>
        { scripts.Add(text); return Task.FromResult<string?>("token:" + new string('a', 32)); }, _ => { }, value => result = value, TimeSpan.Zero);
        await session.PageLoadedAsync(); Assert.Empty(scripts); Assert.Null(result);
        uri = flow.TokensPage; session.NavigationStarted(); await session.PageLoadedAsync();
        Assert.Single(scripts); Assert.Contains("location.origin", scripts[0]);
        Assert.Equal(WebSignInOutcome.Token, result!.Outcome);
        Assert.Equal(new string('a', 32), result.Token);
    }

    [Fact]
    public async Task A_timezone_reload_during_settling_does_not_submit_into_the_next_page()
    {
        var flow = Flow(); var scripts = new List<string>();
        var probed = new TaskCompletionSource();
        using var session = new WebSignInSession(flow, () => flow.TokensPage, text =>
        { scripts.Add(text); probed.TrySetResult(); return Task.FromResult<string?>("form"); }, _ => { }, _ => { }, TimeSpan.FromMilliseconds(50));
        var page = session.PageLoadedAsync(); await probed.Task; session.NavigationStarted(); await page;
        Assert.Single(scripts);
        await session.PageLoadedAsync(); Assert.Equal(3, scripts.Count);
        Assert.Contains("create-api-token-form", scripts[^1]);
    }

    [Fact]
    public async Task A_sent_post_without_a_token_fails_instead_of_creating_another_token()
    {
        var flow = Flow(); var submissions = 0; WebSignInResult? result = null;
        using var session = new WebSignInSession(flow, () => flow.TokensPage,
            text => { if (text.Contains("f.submit")) { submissions++; return Task.FromResult<string?>("submitted"); } return Task.FromResult<string?>("form"); },
            _ => { }, value => result = value, TimeSpan.Zero);
        await session.PageLoadedAsync();
        session.RequestStarted(flow.TokensPage, HttpMethod.Post);
        session.NavigationStarted(); await session.PageLoadedAsync();
        Assert.Equal(1, submissions); Assert.Equal(WebSignInOutcome.Failed, result!.Outcome);
    }

    [Fact]
    public async Task Closing_during_a_probe_cannot_return_a_token_or_submit_a_form()
    {
        var flow = Flow(); var probe = new TaskCompletionSource<string?>(); WebSignInResult? result = null;
        var session = new WebSignInSession(flow, () => flow.TokensPage, _ => probe.Task, _ => { }, value => result = value, TimeSpan.Zero);
        var page = session.PageLoadedAsync(); session.Dispose(); probe.SetResult("token:" + new string('a', 32)); await page;
        Assert.Null(result);
    }
}

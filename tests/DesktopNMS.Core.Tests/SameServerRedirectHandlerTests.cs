using System.Net;
using DesktopNMS.Core.Api;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>The API token only follows a redirect that stays on the same server (mobile issue #1).</summary>
public class SameServerRedirectHandlerTests
{
    /// <summary>Answers each request in turn with the next scripted response, recording what it was sent.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) =>
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);

        public List<(HttpMethod Method, Uri Uri, string? Token, bool HasBody)> Sent { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((
                request.Method,
                request.RequestUri!,
                request.Headers.TryGetValues(SameServerRedirectHandler.TokenHeader, out var values) ? values.Single() : null,
                request.Content is not null));
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Redirect(HttpStatusCode status, string location) =>
        _ => new HttpResponseMessage(status)
        {
            // Explicitly relative for "/...", which Unix would otherwise read as a file path.
            Headers = { Location = new Uri(location, location.StartsWith('/') ? UriKind.Relative : UriKind.Absolute) },
        };

    private static Func<HttpRequestMessage, HttpResponseMessage> Ok() => _ => new HttpResponseMessage(HttpStatusCode.OK);

    private static async Task<(HttpResponseMessage Response, ScriptedHandler Inner)> SendAsync(
        HttpMethod method, string url, params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var inner = new ScriptedHandler(responses);
        using var client = new HttpClient(new SameServerRedirectHandler(inner));
        client.DefaultRequestHeaders.Add(SameServerRedirectHandler.TokenHeader, "SECRET-TOKEN");

        using var request = new HttpRequestMessage(method, url);
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}");
        }

        return (await client.SendAsync(request), inner);
    }

    [Fact]
    public async Task A_redirect_to_another_host_goes_on_without_the_token()
    {
        var (response, inner) = await SendAsync(HttpMethod.Get, "https://nms.example.com/api/v0/devices",
            Redirect(HttpStatusCode.Found, "https://elsewhere.example.net/collect"),
            Ok());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("SECRET-TOKEN", inner.Sent[0].Token);
        Assert.Equal(new Uri("https://elsewhere.example.net/collect"), inner.Sent[1].Uri);
        Assert.Null(inner.Sent[1].Token);
    }

    [Fact]
    public async Task A_redirect_to_another_port_on_the_same_host_goes_on_without_the_token()
    {
        var (_, inner) = await SendAsync(HttpMethod.Get, "https://nms.example.com/api/v0/devices",
            Redirect(HttpStatusCode.Found, "https://nms.example.com:8443/api/v0/devices"),
            Ok());

        Assert.Null(inner.Sent[1].Token);
    }

    [Fact]
    public async Task A_redirect_on_the_same_server_keeps_the_token()
    {
        var (_, inner) = await SendAsync(HttpMethod.Get, "https://nms.example.com/api/v0/devices",
            Redirect(HttpStatusCode.MovedPermanently, "/librenms/api/v0/devices"),
            Ok());

        Assert.Equal(new Uri("https://nms.example.com/librenms/api/v0/devices"), inner.Sent[1].Uri);
        Assert.Equal("SECRET-TOKEN", inner.Sent[1].Token);
    }

    [Fact]
    public async Task Upgrading_the_same_host_from_http_to_https_keeps_the_token()
    {
        var (_, inner) = await SendAsync(HttpMethod.Get, "http://nms.example.com/api/v0/devices",
            Redirect(HttpStatusCode.MovedPermanently, "https://nms.example.com/api/v0/devices"),
            Ok());

        Assert.Equal("SECRET-TOKEN", inner.Sent[1].Token);
    }

    [Fact]
    public async Task Once_dropped_the_token_stays_dropped_even_back_on_the_server()
    {
        var (_, inner) = await SendAsync(HttpMethod.Get, "https://nms.example.com/api/v0/devices",
            Redirect(HttpStatusCode.Found, "https://elsewhere.example.net/hop"),
            Redirect(HttpStatusCode.Found, "https://nms.example.com/api/v0/devices"),
            Ok());

        Assert.Null(inner.Sent[1].Token);
        Assert.Null(inner.Sent[2].Token);
    }

    [Fact]
    public async Task Https_is_never_followed_down_to_http()
    {
        var (response, inner) = await SendAsync(HttpMethod.Get, "https://nms.example.com/api/v0/devices",
            Redirect(HttpStatusCode.Found, "http://nms.example.com/api/v0/devices"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(inner.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.SeeOther, "GET", false)]
    [InlineData(HttpStatusCode.Found, "GET", false)]
    [InlineData(HttpStatusCode.TemporaryRedirect, "POST", true)]
    [InlineData(HttpStatusCode.PermanentRedirect, "POST", true)]
    public async Task A_POST_is_redirected_as_NET_does(HttpStatusCode status, string method, bool keepsBody)
    {
        var (_, inner) = await SendAsync(HttpMethod.Post, "https://nms.example.com/api/v0/devices",
            Redirect(status, "/api/v0/devices/"),
            Ok());

        Assert.Equal(new HttpMethod(method), inner.Sent[1].Method);
        Assert.Equal(keepsBody, inner.Sent[1].HasBody);
    }

    [Theory]
    [InlineData("https://nms.example.com/", "https://NMS.example.com/x", true)]
    [InlineData("https://nms.example.com/", "https://nms.example.com:443/x", true)]
    [InlineData("http://nms.example.com/", "https://nms.example.com/x", true)]
    [InlineData("http://nms.example.com:8080/", "https://nms.example.com/x", true)]
    [InlineData("http://nms.example.com/", "https://nms.example.com:8443/x", false)]
    [InlineData("https://nms.example.com/", "https://nms.example.com.evil.test/x", false)]
    [InlineData("https://nms.example.com/", "https://other.example.com/x", false)]
    public void Same_server_means_the_same_host_and_port_or_an_upgrade_to_https(string origin, string target, bool same) =>
        Assert.Equal(same, SameServerRedirectHandler.SameServer(new Uri(origin), new Uri(target)));
}

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopNMS.Core.Intelligence;

/// <summary>A model the signed-in account can use.</summary>
public sealed record ChatGptModel(string Slug, string DisplayName)
{
    /// <summary>Its name as a list shows it, wherever it's shown as text.</summary>
    public override string ToString() => DisplayName;

    /// <summary>
    /// A readable name from a slug when the API gives none:
    /// "gpt-5.1-codex-mini" → "GPT-5.1 Codex Mini".
    /// </summary>
    public static string NameFor(string slug)
    {
        var parts = slug.Split('-');
        if (parts.Length >= 2 && parts[0].Equals("gpt", StringComparison.OrdinalIgnoreCase))
        {
            var rest = parts.Skip(2).Select(Title);
            return string.Join(' ', new[] { "GPT-" + parts[1] }.Concat(rest));
        }

        return string.Join(' ', parts.Select(Title));

        static string Title(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
    }
}

/// <summary>One server-sent event from a streamed response: its type and its JSON.</summary>
public sealed record ResponseStreamEvent(string Type, JsonObject Data);

/// <summary>
/// OpenAI's public Responses API, called with the ChatGPT account's own
/// access token - never ChatGPT's private backend. Sign in with ChatGPT's
/// preview needs <c>store: false</c> and <c>stream: true</c> on every
/// request, so history is kept here and sent again each turn.
/// </summary>
public sealed class ResponsesClient
{
    public const string ApiBase = "https://api.openai.com/v1/";

    private readonly HttpClient _http;

    public ResponsesClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>The account's models, in the server's order - those meant to be listed.</summary>
    public async Task<IReadOnlyList<ChatGptModel>> ListModelsAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new IntelligenceException(Describe(response.StatusCode, body), response.StatusCode == HttpStatusCode.Unauthorized ? IntelligenceFailure.SignIn : IntelligenceFailure.Model);
        }

        var json = JsonNode.Parse(body);
        var list = json?["models"] as JsonArray ?? json?["data"] as JsonArray ?? new JsonArray();
        return list.OfType<JsonObject>()
            .Where(m => m["visibility"] is null || m["visibility"]!.GetValue<string>() == "list")
            .Select(m => (m["slug"] ?? m["id"])?.GetValue<string>() is { } slug
                ? new ChatGptModel(slug, Text(m, "display_name", "displayName", "title", "name") is { } name && name != slug ? name : ChatGptModel.NameFor(slug))
                : null)
            .OfType<ChatGptModel>()
            .ToList();
    }

    /// <summary>Streams one response's events, ending after completed, failed or incomplete.</summary>
    public async IAsyncEnumerable<ResponseStreamEvent> StreamAsync(string accessToken, JsonObject body, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        body["store"] = false;
        body["stream"] = true;

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new IntelligenceException(Describe(response.StatusCode, error), response.StatusCode == HttpStatusCode.Unauthorized ? IntelligenceFailure.SignIn : IntelligenceFailure.Model);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan(5).TrimStart());
                continue;
            }

            if (line.Length != 0 || data.Length == 0)
            {
                continue;
            }

            // A blank line ends the event.
            var text = data.ToString();
            data.Clear();
            if (text == "[DONE]")
            {
                yield break;
            }

            if (JsonNode.Parse(text) is JsonObject json && json["type"]?.GetValue<string>() is { } type)
            {
                yield return new ResponseStreamEvent(type, json);
                if (type is "response.completed" or "response.failed" or "response.incomplete")
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>The first of <paramref name="keys"/> that's a non-empty string.</summary>
    private static string? Text(JsonObject json, params string[] keys)
        => keys.Select(k => json[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null)
               .FirstOrDefault(s => s is not null);

    private static string Describe(HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => "The ChatGPT sign-in has expired. Sign in again.",
            HttpStatusCode.TooManyRequests => "ChatGPT's usage limit for this plan has been reached. Check Manage usage, or try again later.",
            _ => $"ChatGPT returned an error (HTTP {(int)status}){(message is null ? "." : ": " + message)}",
        };
    }
}

/// <summary>Which part failed - so the panel can say what to do about it.</summary>
public enum IntelligenceFailure
{
    SignIn,
    PlanUsage,
    Model,
    Tools,
    Network,
}

public sealed class IntelligenceException : Exception
{
    public IntelligenceException(string message, IntelligenceFailure failure)
        : base(message)
    {
        Failure = failure;
    }

    public IntelligenceFailure Failure { get; }
}

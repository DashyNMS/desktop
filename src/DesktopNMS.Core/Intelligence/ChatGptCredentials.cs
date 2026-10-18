using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// One ChatGPT sign-in: the client ID OpenAI issued to this installation,
/// and the account's tokens. Stored encrypted (see
/// <see cref="IChatGptCredentialStore"/>) and never logged.
/// </summary>
public sealed class ChatGptCredentials
{
    /// <summary>This installation's opaque host ID ("urn:uuid:…"), sent as ext_agent_host_id.</summary>
    [JsonPropertyName("host_id")]
    public string HostId { get; set; } = string.Empty;

    /// <summary>The client ID OpenAI issued on first registration ("oaiapp_…") - used for every later sign-in.</summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = new();

    /// <summary>The validated ID token's subject - the account's identity.</summary>
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonIgnore]
    public bool IsSignedIn => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken);

    /// <summary>Signed in is not the same as allowed to use the plan: that's its own scope.</summary>
    [JsonIgnore]
    public bool CanUsePlan => Scopes.Contains(ChatGptSignIn.PlanUsageScope);
}

/// <summary>Where the sign-in lives - encrypted for the current Windows user on desktop.</summary>
public interface IChatGptCredentialStore
{
    ChatGptCredentials? Load();

    void Save(ChatGptCredentials credentials);

    void Clear();
}

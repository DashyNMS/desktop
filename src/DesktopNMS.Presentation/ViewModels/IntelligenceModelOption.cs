using System;
using System.Collections.Generic;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Services.Intelligence.Agents;

namespace DesktopNMS.ViewModels;

/// <summary>
/// A model in the composer's picker (#316): which provider answers with it, the
/// id that provider knows it by, and the reasoning levels it accepts. Equal by
/// provider and id, so a restored chat's model matches the listed one.
/// </summary>
public sealed record IntelligenceModelOption(string Provider, string Id, string Name, IReadOnlyList<string> Efforts, string? DefaultEffort = null, string? Description = null)
{
    public bool HasEfforts => Efforts is { Count: > 0 };

    public static IntelligenceModelOption FromChatGpt(ChatGptModel model)
        => new(AppSettings.IntelligenceProviderChatGpt, model.Slug, model.DisplayName, Array.Empty<string>());

    public static IntelligenceModelOption FromAgent(string provider, AgentModel model)
        => new(provider, model.Id, model.Name, model.Efforts, model.DefaultEffort, model.Description);

    /// <summary>A model a saved chat names that its provider no longer lists - kept so the chat still says what it used.</summary>
    public static IntelligenceModelOption Unlisted(string provider, string id)
        => new(provider, id, provider == AppSettings.IntelligenceProviderChatGpt ? ChatGptModel.NameFor(id) : id, Array.Empty<string>());

    public bool Equals(IntelligenceModelOption? other)
        => other is not null && Provider == other.Provider && Id == other.Id;

    public override int GetHashCode() => HashCode.Combine(Provider, Id);

    public override string ToString() => Name;

    /// <summary>A reasoning level as the picker shows it.</summary>
    public static string EffortName(string effort) => effort switch
    {
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra high",
        "max" => "Max",
        _ => effort.Length == 0 ? effort : char.ToUpperInvariant(effort[0]) + effort[1..],
    };
}

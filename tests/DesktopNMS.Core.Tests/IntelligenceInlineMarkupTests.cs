using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class IntelligenceInlineMarkupTests
{
    [Fact]
    public void Uncertainty_footer_copies_as_readable_markdown_with_resolved_names()
    {
        var source = "[[update]] Impact on [[device:12]] remains unverified. `[[update]]`";
        Assert.Equal("**Notes:**  Impact on router-a (Down) remains unverified. `[[update]]`",
            IntelligenceInlineMarkup.ToReadableMarkdown(source, References));
        Assert.StartsWith("Notes:  Impact on router-a (Down)", IntelligenceInlineMarkup.ToReadableText(source, References));
    }
    private static readonly IntelligenceInlineReference[] References =
    [
        new("device:12", "router-a", "Down", "down", DateTimeOffset.UtcNow, "Observed device status"),
        new("alert:4", "On battery", "Critical", "critical", DateTimeOffset.UtcNow, "Acknowledged"),
    ];

    [Fact]
    public void Presentation_markers_copy_as_ordinary_bullets_and_a_readable_recommendation()
    {
        var source = "- [[bullet:critical]] [[alert:4]] on [[device:12]].\n\n[[recommendation]] Check the power feed.";
        var copy = IntelligenceInlineMarkup.ToReadableMarkdown(source, References);
        Assert.DoesNotContain("[[", copy);
        Assert.Contains("On battery (Critical)", copy);
        Assert.Contains("**Recommendation:**", copy);
        Assert.Contains("Recommendation:", IntelligenceInlineMarkup.ToReadableText(source, References));
        Assert.Equal("critical", IntelligenceInlineMarkup.Resolve("[[bullet:critical]]", [])!.Tone);
    }

    [Fact]
    public void Object_markers_resolve_only_against_confirmed_ids()
    {
        Assert.Equal("router-a", IntelligenceInlineMarkup.Resolve("[[device:12]]", References)!.Name);
        Assert.Equal("Critical", IntelligenceInlineMarkup.Resolve("[[alert:4]]", References)!.Status);
        Assert.Null(IntelligenceInlineMarkup.Resolve("[[device:999]]", References));
        Assert.Null(IntelligenceInlineMarkup.Resolve("[[device:4]]", References));
        Assert.Null(IntelligenceInlineMarkup.Resolve("[[device:12]]untrusted suffix", References));
    }

    [Theory]
    [InlineData("[[status:up]]", "Up", "up")]
    [InlineData("[[status:down]]", "Down", "down")]
    [InlineData("[[severity:critical]]", "Critical", "critical")]
    [InlineData("[[severity:warning]]", "Warning", "warning")]
    public void Display_labels_have_a_fixed_vocabulary(string marker, string label, string tone)
    {
        var resolved = IntelligenceInlineMarkup.Resolve(marker, []);
        Assert.Equal(label, resolved!.Name);
        Assert.Equal(tone, resolved.Tone);
        Assert.Equal(default, resolved.ObservedAt);
    }

    [Theory]
    [InlineData("[[status:critical]]")]
    [InlineData("[[severity:up]]")]
    [InlineData("[[run:delete]]")]
    [InlineData("[[device:https://example.org]]")]
    public void Unsupported_markers_cannot_create_arbitrary_ui_or_actions(string marker)
        => Assert.Null(IntelligenceInlineMarkup.Resolve(marker, References));

    [Fact]
    public void Readable_copy_preserves_surrounding_markdown_and_literal_code()
    {
        var source = "**Check [[device:12]]** and [[alert:4]]. `[[device:12]]`\n\n```\n[[device:12]]\n```";
        var copy = IntelligenceInlineMarkup.ToReadableMarkdown(source, References);
        Assert.StartsWith("**Check router-a (Down)** and On battery (Critical).", copy);
        Assert.Contains("`[[device:12]]`", copy);
        Assert.Contains("```\n[[device:12]]\n```", copy);
        Assert.Equal("Device 999", IntelligenceInlineMarkup.ToReadableText("[[device:999]]", References));
    }

    [Fact]
    public void Alert_snapshots_supply_both_device_and_alert_references_without_conflating_states()
    {
        var snapshot = new IntelligenceStatusCard("Alerts", "Scope", DateTimeOffset.UtcNow,
            [new("ups-a", "On battery · Acknowledged", "Critical", "critical", "Up", 12, "On battery", AlertId: 4)]);
        var refs = IntelligenceInlineReferences.FromSnapshot(snapshot).ToArray();
        Assert.Equal("Up", refs.Single(r => r.Key == "device:12").Status);
        Assert.Equal("Critical", refs.Single(r => r.Key == "alert:4").Status);
    }

    [Theory]
    [InlineData("Critical - Device Offline", "Critical", "Device Offline")]
    [InlineData("Warning – Temperature", "Warning", "Temperature")]
    [InlineData("Critical power feed", "Critical", "Critical power feed")]
    [InlineData("Critical - Temperature", "Warning", "Critical - Temperature")]
    public void Alert_display_names_remove_only_a_matching_redundant_severity_prefix(string rule, string severity, string expected)
    {
        var snapshot = new IntelligenceStatusCard("Alert", "Scope", DateTimeOffset.UtcNow,
            [new("router-a", rule, severity, "critical", "Up", 12, rule, AlertId: 4)]);
        Assert.Equal(expected, IntelligenceInlineReferences.FromSnapshot(snapshot).Single(r => r.Key == "alert:4").Name);
    }
}

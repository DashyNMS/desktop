using DesktopNMS.Core.Intelligence;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// Device markers no lookup named take their name from the app's device list,
/// so an answer never shows a bare "Device 442".
/// </summary>
public sealed class IntelligenceDeviceNamingTests
{
    private static IntelligenceInlineReference? Cached(int id) => id == 442
        ? new("device:442", "edge-rtr-01.example.net", "Up", "up", DateTimeOffset.UtcNow, "Device status from DashyNMS's device list") { Object = new("device", 442) }
        : null;

    [Fact]
    public void An_unnamed_device_marker_is_named_from_the_device_list()
    {
        var message = IntelligenceMessageViewModel.Assistant();
        message.Text = "[[device:442]] came back online.";

        message.NameDevices(Cached);

        Assert.Equal("edge-rtr-01.example.net (Up) came back online.", IntelligenceInlineMarkup.ToReadableText(message.Text, message.InlineReferences));
    }

    [Fact]
    public void A_lookup_name_wins_over_the_device_list()
    {
        var message = IntelligenceMessageViewModel.Assistant();
        message.AddStatusCard(new("Device status", "Test scope", DateTimeOffset.UtcNow,
            new[] { new IntelligenceStatusRow("core-sw-01.example.net", "Test location", "Down", "down", DeviceId: 442) }));
        message.Text = "[[device:442]] is down.";

        message.NameDevices(Cached);

        Assert.StartsWith("core-sw-01.example.net", IntelligenceInlineMarkup.ToReadableText(message.Text, message.InlineReferences));
    }

    [Fact]
    public void The_name_is_kept_when_the_chat_is_reopened()
    {
        var message = IntelligenceMessageViewModel.Assistant();
        message.Text = "[[device:442]] came back online.";
        message.NameDevices(Cached);

        var restored = IntelligenceMessageViewModel.Restore(message.Save());

        Assert.Contains("edge-rtr-01.example.net", IntelligenceInlineMarkup.ToReadableText(restored.Text, restored.InlineReferences));
    }

    [Fact]
    public void A_device_missing_from_the_list_is_left_alone()
    {
        var message = IntelligenceMessageViewModel.Assistant();
        message.Text = "[[device:7]] is quiet.";

        message.NameDevices(Cached);

        Assert.Empty(message.InlineReferences);
    }
}

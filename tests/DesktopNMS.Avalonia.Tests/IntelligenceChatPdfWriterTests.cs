using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using DesktopNMS.Services;
using DesktopNMS.Services.Intelligence;
using SkiaSharp;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

/// <summary>An Intelligence chat saved as a PDF (#315).</summary>
public sealed class IntelligenceChatPdfWriterTests
{
    [AvaloniaFact]
    public void A_long_chat_runs_over_pages_with_its_sources_clickable()
    {
        var answer = string.Join("\n\n", Enumerable.Range(1, 80).Select(i =>
            $"Paragraph {i} about **core-sw-{i:00}**, with a [source](https://docs.example.net/{i}) and a long name: {new string('x', 140)}."));
        answer += "\n\n## Ports\n\n| Device | Status |\n|---|---:|\n| core-sw-01 | up |\n| Ünïcödé 東京 | down |\n\n"
            + "```\nshow interfaces status\n```\n\n- one\n  - nested\n1. first\n\n> A quote\n\n---";
        using var bitmap = new SKBitmap(400, 150);
        bitmap.Erase(SKColors.SteelBlue);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var chat = new ExportedChat("Synthetic chat", DateTimeOffset.UnixEpoch, new[]
        {
            new ExportedChatMessage(true, DateTimeOffset.UnixEpoch, "How is the network?\n\nAnd core-sw-01?", Array.Empty<ExportedChatImage>(), null),
            new ExportedChatMessage(false, DateTimeOffset.UnixEpoch, answer,
                new[] { new ExportedChatImage(png.ToArray(), "image/png"), new ExportedChatImage(new byte[] { 1, 2, 3 }, "image/png") },
                "This answer was stopped before it finished."),
        });

        using var output = new MemoryStream();
        new IntelligenceChatPdfWriter().Write(chat, output);

        var pdf = Encoding.Latin1.GetString(output.ToArray());
        Assert.StartsWith("%PDF-", pdf);
        Assert.Contains("https://docs.example.net/80", pdf);
        Assert.True(Regex.Matches(pdf, @"/Type\s*/Page\b").Count >= 2);
    }
}

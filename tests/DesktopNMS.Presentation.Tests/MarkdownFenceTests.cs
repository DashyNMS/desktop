using DesktopNMS.Services.Intelligence;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Fenced code as CommonMark reads it, so an answer's code never swallows the text after it.</summary>
public sealed class MarkdownFenceTests
{
    [Theory]
    [InlineData("```", '`', 3)]
    [InlineData("````text", '`', 4)]
    [InlineData("~~~", '~', 3)]
    [InlineData("  ~~~~~ yaml", '~', 5)]
    [InlineData("~~~ uses `backticks`", '~', 3)]
    public void Backtick_and_tilde_fences_open_code(string line, char marker, int length)
    {
        Assert.True(MarkdownFence.TryOpen(line, out var fence));
        Assert.Equal(new MarkdownFence(marker, length), fence);
    }

    [Theory]
    [InlineData("``")]
    [InlineData("~~")]
    [InlineData("```inline `code` here")]
    [InlineData("text ```")]
    public void Other_lines_never_open_code(string line) => Assert.False(MarkdownFence.TryOpen(line, out _));

    [Theory]
    [InlineData("```", true)]
    [InlineData("`````", true)]
    [InlineData("```  ", true)]
    [InlineData("``", false)]
    [InlineData("~~~", false)]
    [InlineData("```json", false)]
    public void Only_a_matching_fence_at_least_as_long_closes_code(string line, bool closes)
        => Assert.Equal(closes, new MarkdownFence('`', 3).IsClosedBy(line));

    [Fact]
    public void A_longer_fence_keeps_shorter_and_labelled_fences_inside_its_code()
    {
        var blocks = ChatMarkdownBlocks.Parse("````\n```json\n{}\n```\n````\nAfter the code.");

        var code = Assert.IsType<ChatCodeBlock>(blocks[0]);
        Assert.Equal("```json\n{}\n```", code.Text);
        Assert.IsNotType<ChatCodeBlock>(blocks[1]);
    }

    [Fact]
    public void A_tilde_fence_renders_as_code_in_the_pdf_blocks()
    {
        var blocks = ChatMarkdownBlocks.Parse("~~~\nshow run\n```\n~~~\nDone.");

        Assert.Equal("show run\n```", Assert.IsType<ChatCodeBlock>(blocks[0]).Text);
    }

    [Theory]
    [InlineData("Answer\n```\ncode", '`', 3)]
    [InlineData("Answer\n~~~~\ncode\n~~~", '~', 4)]
    public void An_answer_stopped_mid_code_reports_its_open_fence(string markdown, char marker, int length)
        => Assert.Equal(new MarkdownFence(marker, length), MarkdownFence.Unclosed(markdown));

    [Fact]
    public void Code_indented_four_spaces_is_not_a_fence_for_the_export_but_still_code_on_screen()
    {
        Assert.True(MarkdownFence.TryOpen("   ```", out _));
        Assert.False(MarkdownFence.TryOpen("    ```", out _));
        Assert.False(MarkdownFence.TryOpen("\t```", out _));
        Assert.False(new MarkdownFence('`', 3).IsClosedBy("    ```"));
        Assert.Null(MarkdownFence.Unclosed("- step\n    ```\n    show run"));

        // On screen there are no nested blocks, so code under a list item still renders as code.
        Assert.True(MarkdownFence.TryOpen("    ```", out _, nested: true));
        Assert.Contains(ChatMarkdownBlocks.Parse("- step\n    ```\n    show run\n    ```"), block => block is ChatCodeBlock);
    }

    [Fact]
    public void A_finished_answer_has_no_open_fence()
        => Assert.Null(MarkdownFence.Unclosed("```\ncode\n```\n~~~\nmore\n~~~"));

    [Fact]
    public void An_answer_interrupted_by_closing_the_app_shows_its_note_outside_the_code_it_stopped_in()
    {
        var restored = DesktopNMS.ViewModels.IntelligenceMessageViewModel.Restore(new DesktopNMS.Core.Intelligence.SavedIntelligenceMessage
        {
            Text = "Config:\n~~~\ninterface Gi0/1\n```",
            IsIncomplete = true,
        });

        var blocks = ChatMarkdownBlocks.Parse(restored.Text);

        Assert.Equal("interface Gi0/1\n```", Assert.IsType<ChatCodeBlock>(blocks[1]).Text);
        Assert.IsNotType<ChatCodeBlock>(blocks[^1]);
        Assert.EndsWith("(Interrupted when DashyNMS closed.)", restored.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void In_an_export_a_later_question_stays_outside_an_earlier_answer_s_unfinished_code()
    {
        var chat = new ExportedChat("Chat", DateTimeOffset.UnixEpoch,
        [
            new(true, null, "First question", [], null),
            new(false, null, "Here is the config:\n````\ninterface Gi0/1\n```\n# not a heading", [], "Stopped"),
            new(true, null, "Second question", [], null),
            new(false, null, "## Second answer", [], null),
        ]);

        var markdown = IntelligenceChatMarkdown.Write(chat, (_, _, _) => "");

        // Parsed back, the second question and answer are ordinary blocks, not code.
        var blocks = ChatMarkdownBlocks.Parse(markdown);
        var code = Assert.Single(blocks.OfType<ChatCodeBlock>());
        Assert.Equal("interface Gi0/1\n```\n# not a heading", code.Text);
        Assert.Contains("````\n\n---", markdown, StringComparison.Ordinal);
        Assert.Contains("#### Second answer", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("### not a heading", markdown, StringComparison.Ordinal);
    }
}

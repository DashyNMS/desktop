using DesktopNMS.Core.Intelligence;
using DesktopNMS.Services.Intelligence;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Saving an Intelligence chat as Markdown, and reading answers for the PDF (#315).</summary>
public sealed class IntelligenceChatExportTests
{
    private const string Answer = "# Summary\n\n- **Up** for 3 days\n  - core_sw_01 uplink\n1. First\n1. Second\n\n"
        + "See [the guide](https://docs.example.net/guide).\n\n| Device | Status |\n|---|---:|\n| core-sw-01 | up |\n\n```\n# show run\n```";

    [Fact]
    public void Markdown_keeps_the_order_and_formatting_and_leaves_out_lookups()
    {
        var question = IntelligenceMessageViewModel.User("How is core-sw-01?");
        var answer = IntelligenceMessageViewModel.Assistant();
        answer.Text = Answer;
        answer.RecordToolObservation(new IntelligenceToolObservation("device_details", DateTimeOffset.UtcNow, "synthetic raw lookup record"));
        answer.Finish(IntelligenceReplyOutcome.Completed);

        var chat = ExportedChat.From("Core switch check", new[] { question, answer }, DateTimeOffset.UtcNow);
        var markdown = IntelligenceChatMarkdown.Write(chat, (_, _, _) => throw new InvalidOperationException("No images."));

        Assert.StartsWith("# Core switch check\n", markdown);
        Assert.True(markdown.IndexOf("## You", StringComparison.Ordinal) < markdown.IndexOf("## Dashy", StringComparison.Ordinal));
        Assert.Contains("\n### Summary\n", markdown);
        Assert.Contains("[the guide](https://docs.example.net/guide)", markdown);
        Assert.Contains("| core-sw-01 | up |", markdown);
        Assert.Contains("```\n# show run\n```", markdown);
        Assert.DoesNotContain("synthetic raw lookup record", markdown);
        Assert.DoesNotContain("Note:", markdown);
    }

    [Theory]
    [InlineData(null, "still being written")]
    [InlineData(IntelligenceReplyOutcome.Stopped, "stopped before it finished")]
    [InlineData(IntelligenceReplyOutcome.Interrupted, "interrupted before it finished")]
    public void An_unfinished_answer_says_so(IntelligenceReplyOutcome? outcome, string note)
    {
        var answer = IntelligenceMessageViewModel.Assistant();
        answer.Text = "Partial answer";
        if (outcome is { } finished) answer.Finish(finished);

        var chat = ExportedChat.From("Chat", new[] { answer }, DateTimeOffset.UtcNow);

        Assert.Contains(note, Assert.Single(chat.Messages).Note);
        Assert.Contains("Partial answer", IntelligenceChatMarkdown.Write(chat, (_, _, _) => ""));
    }

    [Fact]
    public void Graphs_are_saved_beside_the_file_and_linked_by_relative_paths()
    {
        var folder = Directory.CreateTempSubdirectory("dashynms-export-");
        try
        {
            var answer = IntelligenceMessageViewModel.Assistant();
            answer.AddImage(new IntelligenceImage(new byte[] { 1, 2, 3 }, "image/png"));
            answer.AddImage(new IntelligenceImage(new byte[] { 4, 5 }, "image/jpeg"));
            answer.Text = "Traffic is normal.";
            answer.Finish(IntelligenceReplyOutcome.Completed);
            var path = Path.Combine(folder.FullName, "My chat.md");

            new IntelligenceChatExporter().SaveMarkdown(ExportedChat.From("My chat", new[] { answer }, DateTimeOffset.UtcNow), path);

            var markdown = File.ReadAllText(path);
            Assert.Contains("![Graph 1](My%20chat-images/answer-1-graph-1.png)", markdown);
            Assert.Contains("![Graph 2](My%20chat-images/answer-1-graph-2.jpg)", markdown);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(folder.FullName, "My chat-images", "answer-1-graph-1.png")));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_question_is_plain_text_so_its_symbols_are_escaped()
    {
        var chat = ExportedChat.From("Chat", new[] { IntelligenceMessageViewModel.User("# not a heading\n1. not a list *or* [link]") }, DateTimeOffset.UtcNow);
        var markdown = IntelligenceChatMarkdown.Write(chat, (_, _, _) => "");

        Assert.Contains("\\# not a heading  \n1\\. not a list \\*or\\* \\[link\\]", markdown);
    }

    [Fact]
    public void A_title_becomes_a_safe_file_name()
    {
        var stem = IntelligenceChatExporter.FileStem("Why is: core/sw*01 \"down\"?");

        Assert.Equal("Why is core sw 01 down", stem);
        Assert.Equal("Intelligence chat", IntelligenceChatExporter.FileStem("..."));
    }

    [Fact]
    public void Answers_are_read_into_blocks_for_the_pdf()
    {
        var blocks = ChatMarkdownBlocks.Parse(Answer);

        Assert.Equal(1, Assert.IsType<ChatHeadingBlock>(blocks[0]).Level);
        var items = blocks.OfType<ChatListItemBlock>().ToList();
        Assert.Equal(new[] { 0, 1, 0, 0 }, items.Select(item => item.Depth));
        Assert.Equal(new[] { "•", "•", "1.", "2." }, items.Select(item => item.Marker));
        Assert.Equal("core_sw_01 uplink", Assert.Single(items[1].Spans).Text);
        Assert.Equal(ChatTextStyle.Bold, items[0].Spans[0].Style);

        var link = blocks.OfType<ChatParagraphBlock>().Single().Spans.Single(span => span.Link is not null);
        Assert.Equal("the guide", link.Text);
        Assert.Equal("https://docs.example.net/guide", link.Link!.AbsoluteUri);

        var table = blocks.OfType<ChatTableBlock>().Single();
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(ChatColumnAlignment.Right, table.Alignments[1]);
        Assert.Equal("# show run", blocks.OfType<ChatCodeBlock>().Single().Text);
    }

    [Fact]
    public void Only_web_addresses_become_links_and_escapes_are_undone()
    {
        var spans = ChatMarkdownBlocks.Inline("[file](file:///etc/hosts) and core\\_sw\\_01 and `code`");

        Assert.All(spans, span => Assert.Null(span.Link));
        Assert.Equal("file and core_sw_01 and ", spans[0].Text);
        Assert.Equal(ChatTextStyle.Code, spans[1].Style);
    }
}

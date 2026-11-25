using DesktopNMS.Services.Help;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Reading a help page's Markdown into what the app draws (#604). Made-up pages only.</summary>
public sealed class HelpBlocksTests
{
    [Fact]
    public void Headings_paragraphs_rules_quotes_and_images_are_blocks()
    {
        var blocks = HelpBlocks.Read("First line\ncarries on.\n\n## Find it\n\n![The *list*](../list.png)\n\n---\n\n> Take care.\n> Really.");

        Assert.Equal("First line carries on.", Text(Assert.IsType<HelpParagraphBlock>(blocks[0]).Spans));
        Assert.Equal((2, "Find it"), (Assert.IsType<HelpHeadingBlock>(blocks[1]).Level, ((HelpHeadingBlock)blocks[1]).Text));
        Assert.Equal(new HelpImageBlock("The *list*", "../list.png"), blocks[2]);
        Assert.IsType<HelpRuleBlock>(blocks[3]);
        Assert.Equal("Take care. Really.", Text(Assert.IsType<HelpParagraphBlock>(Assert.Single(Assert.IsType<HelpQuoteBlock>(blocks[4]).Blocks)).Spans));
        Assert.Equal(5, blocks.Count);
    }

    [Fact]
    public void Styles_code_and_links_are_spans_with_the_links_target_as_written()
    {
        var spans = HelpBlocks.Spans("Choose **Apply & Save**, `MCP_*` or [the *guide*](a/b.md#c). See <https://example.net/x>. A snake_case_name \\*stays\\*.");

        Assert.Contains(new HelpSpan("Apply & Save", IsBold: true), spans);
        Assert.Contains(new HelpSpan("MCP_*", IsCode: true), spans);
        Assert.Contains(new HelpSpan("the ", Link: "a/b.md#c"), spans);
        Assert.Contains(new HelpSpan("guide", IsItalic: true, Link: "a/b.md#c"), spans);
        Assert.Contains(new HelpSpan("https://example.net/x", Link: "https://example.net/x"), spans);
        Assert.EndsWith(". A snake_case_name *stays*.", Text(spans), StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_item_holds_whatever_is_indented_under_it()
    {
        var list = Assert.IsType<HelpListBlock>(Assert.Single(HelpBlocks.Read(
            "3. First:\n   - inner one\n   - inner two\n\n   ```bash\n   run it\n   ```\n4. Second\n   carries on.\n\n5. Third")));

        Assert.Equal(3, list.Start);
        Assert.Equal(3, list.Items.Count);
        Assert.Equal(2, Assert.IsType<HelpListBlock>(list.Items[0][1]).Items.Count);
        Assert.Equal("run it", Assert.IsType<HelpCodeBlock>(list.Items[0][2]).Text);
        Assert.Equal("Second carries on.", Text(Assert.IsType<HelpParagraphBlock>(Assert.Single(list.Items[1])).Spans));
    }

    [Fact]
    public void Bullets_end_where_the_text_below_starts()
    {
        var blocks = HelpBlocks.Read("- One\n- Two\n\nAfter.");

        Assert.Null(Assert.IsType<HelpListBlock>(blocks[0]).Start);
        Assert.Equal(2, ((HelpListBlock)blocks[0]).Items.Count);
        Assert.Equal("After.", Text(Assert.IsType<HelpParagraphBlock>(blocks[1]).Spans));
    }

    [Fact]
    public void Tables_keep_their_header_and_escaped_bars()
    {
        var table = Assert.IsType<HelpTableBlock>(Assert.Single(HelpBlocks.Read("| Name | What |\n| --- | :-: |\n| `a` | one \\| two |")));

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Name", Text(table.Rows[0][0]));
        Assert.Equal("one | two", Text(table.Rows[1][1]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("```\nnever closed")]
    [InlineData("> > > > > > > > > > > > deep")]
    [InlineData("- - - - - - - - - - - - deep")]
    [InlineData("1.\n2.\n")]
    [InlineData("| a |\n| - |")]
    public void Odd_pages_never_stop_the_reader(string markdown) => Assert.NotNull(HelpBlocks.Read(markdown));

    private static string Text(IEnumerable<HelpSpan> spans) => string.Concat(spans.Select(span => span.Text));
}

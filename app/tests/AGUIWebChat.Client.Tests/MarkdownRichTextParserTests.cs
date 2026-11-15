using AGUIWebChat.Client.Formatting;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class MarkdownRichTextParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Parse_WhenInputIsBlank_ShouldReturnEmpty(string? markdown)
    {
        Assert.Empty(MarkdownRichTextParser.Parse(markdown));
    }

    [Fact]
    public void Parse_WhenHeading_ShouldCarryLevelAndText()
    {
        var nodes = MarkdownRichTextParser.Parse("### Title");

        var heading = Assert.IsType<HeadingNode>(Assert.Single(nodes));
        Assert.Equal(3, heading.Level);
        Assert.Equal("Title", TextOf(heading));
    }

    [Fact]
    public void Parse_WhenParagraphHasInlineFormatting_ShouldNestNodes()
    {
        var nodes = MarkdownRichTextParser.Parse("plain *em* **strong** `code` ~~gone~~");

        var paragraph = Assert.IsType<ParagraphNode>(Assert.Single(nodes));
        var children = paragraph.Children;

        Assert.Contains(children, n => n is EmphasisNode);
        Assert.Contains(children, n => n is StrongNode);
        Assert.Contains(children, n => n is InlineCodeNode { Code: "code" });
        Assert.Contains(children, n => n is StrikethroughNode);
        Assert.Equal("plain em strong  gone", TextOf(paragraph));
    }

    [Fact]
    public void Parse_WhenFencedCode_ShouldCarryLanguageAndCode()
    {
        var nodes = MarkdownRichTextParser.Parse("```csharp\nvar x = 1;\n```");

        var code = Assert.IsType<CodeBlockNode>(Assert.Single(nodes));
        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = 1;", code.Code);
    }

    [Fact]
    public void Parse_WhenOrderedList_ShouldCarryStartAndItems()
    {
        var nodes = MarkdownRichTextParser.Parse("3. three\n4. four");

        var list = Assert.IsType<ListNode>(Assert.Single(nodes));
        Assert.True(list.Ordered);
        Assert.Equal(3, list.Start);
        Assert.Equal(2, list.Children.Count);
        Assert.All(list.Children, n => Assert.IsType<ListItemNode>(n));
    }

    [Fact]
    public void Parse_WhenUnorderedList_ShouldNotBeOrdered()
    {
        var nodes = MarkdownRichTextParser.Parse("- one\n- two");

        var list = Assert.IsType<ListNode>(Assert.Single(nodes));
        Assert.False(list.Ordered);
        Assert.Null(list.Start);
    }

    [Fact]
    public void Parse_WhenTaskList_ShouldCarryCheckedState()
    {
        var nodes = MarkdownRichTextParser.Parse("- [x] done\n- [ ] todo");

        var list = Assert.IsType<ListNode>(Assert.Single(nodes));
        var items = list.Children.Cast<ListItemNode>().ToList();

        Assert.True(items[0].Checked);
        Assert.False(items[1].Checked);
        Assert.Equal("done", TextOf(items[0]).Trim());
    }

    [Fact]
    public void Parse_WhenBlockQuote_ShouldNestParagraph()
    {
        var nodes = MarkdownRichTextParser.Parse("> quoted");

        var quote = Assert.IsType<BlockQuoteNode>(Assert.Single(nodes));
        Assert.IsType<ParagraphNode>(Assert.Single(quote.Children));
        Assert.Equal("quoted", TextOf(quote));
    }

    [Fact]
    public void Parse_WhenTable_ShouldCarryAlignmentsAndRows()
    {
        var nodes = MarkdownRichTextParser.Parse(
            "| a | b | c |\n|:--|:-:|--:|\n| 1 | 2 | 3 |");

        var table = Assert.IsType<TableNode>(Assert.Single(nodes));
        Assert.Equal(
            [TableColumnAlignment.Left, TableColumnAlignment.Center, TableColumnAlignment.Right],
            table.Alignment);

        Assert.Equal(2, table.Children.Count);
        Assert.All(table.Children, n => Assert.IsType<TableRowNode>(n));
        Assert.All(table.Children[0].Children, n => Assert.IsType<TableCellNode>(n));
    }

    [Fact]
    public void Parse_WhenImage_ShouldProduceImageNodeWithAlt()
    {
        var nodes = MarkdownRichTextParser.Parse("![alt text](https://example.com/a.png \"t\")");

        var paragraph = Assert.IsType<ParagraphNode>(Assert.Single(nodes));
        var image = Assert.IsType<ImageNode>(Assert.Single(paragraph.Children));
        Assert.Equal("https://example.com/a.png", image.Url);
        Assert.Equal("alt text", image.Alt);
        Assert.Equal("t", image.Title);
    }

    [Fact]
    public void Parse_WhenLink_ShouldProduceLinkNodeWithText()
    {
        var nodes = MarkdownRichTextParser.Parse("[docs](https://example.com)");

        var paragraph = Assert.IsType<ParagraphNode>(Assert.Single(nodes));
        var link = Assert.IsType<LinkNode>(Assert.Single(paragraph.Children));
        Assert.Equal("https://example.com", link.Url);
        Assert.Equal("docs", TextOf(link));
    }

    [Fact]
    public void Parse_WhenThematicBreak_ShouldProduceThematicBreakNode()
    {
        var nodes = MarkdownRichTextParser.Parse("---");

        Assert.IsType<ThematicBreakNode>(Assert.Single(nodes));
    }

    [Fact]
    public void Parse_WhenMultipleBlocks_ShouldPreserveOrder()
    {
        var nodes = MarkdownRichTextParser.Parse("# h\n\ntext\n\n```\ncode\n```");

        Assert.Collection(
            nodes,
            n => Assert.IsType<HeadingNode>(n),
            n => Assert.IsType<ParagraphNode>(n),
            n => Assert.IsType<CodeBlockNode>(n));
    }

    [Fact]
    public void Parse_WhenRawHtml_ShouldNotEmitHtmlNodes()
    {
        var nodes = MarkdownRichTextParser.Parse("<script>alert(1)</script>");

        Assert.DoesNotContain(nodes, n => n is HtmlNode);
    }

    [Fact]
    public async Task FormattedChatClient_ShouldAttachRichTextContentPerUpdate()
    {
        var inner = new ScriptedChatClient(
            new ChatResponseUpdate(ChatRole.Assistant, "## Head"),
            new ChatResponseUpdate(ChatRole.Assistant, "ing"));
        var client = new FormattedChatClient(inner);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);

        var first = Assert.Single(updates[0].Contents.OfType<RichTextContent>());
        Assert.Equal("## Head", first.Text);
        Assert.IsType<HeadingNode>(Assert.Single(first.Nodes));

        var second = Assert.Single(updates[1].Contents.OfType<RichTextContent>());
        Assert.Equal("## Heading", second.Text);
        var heading = Assert.IsType<HeadingNode>(Assert.Single(second.Nodes));
        Assert.Equal("Heading", TextOf(heading));
    }

    [Fact]
    public async Task FormattedChatClient_ShouldNotAttachContentForReasoningOnlyUpdates()
    {
        var inner = new ScriptedChatClient(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking")]));
        var client = new FormattedChatClient(inner);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Empty(updates[0].Contents.OfType<RichTextContent>());
    }

    private static string TextOf(RichTextNode node)
    {
        var builder = new System.Text.StringBuilder();
        Collect(node, builder);
        return builder.ToString();
    }

    private static void Collect(RichTextNode node, System.Text.StringBuilder builder)
    {
        if (node is TextNode text)
        {
            builder.Append(text.Text);
        }

        foreach (var child in node.Children)
        {
            Collect(child, builder);
        }
    }

    private sealed class ScriptedChatClient(params ChatResponseUpdate[] updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

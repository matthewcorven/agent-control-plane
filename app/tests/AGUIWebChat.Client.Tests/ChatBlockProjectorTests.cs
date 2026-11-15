using AGUIWebChat.Client.Formatting;
using Microsoft.AspNetCore.Components.AI;
using System.Reflection;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class ChatBlockProjectorTests
{
    [Fact]
    public void NodesOf_WhenBlockHasNoContent_ShouldReturnNull()
    {
        var block = new RichContentBlock();

        Assert.Null(ChatBlockProjector.NodesOf(block));
    }

    [Fact]
    public void NodesOf_WhenBlockHasContent_ShouldReturnTheNodes()
    {
        var nodes = MarkdownRichTextParser.Parse("## Heading\n\nBody text.");
        var block = WithContent(nodes);

        var projected = ChatBlockProjector.NodesOf(block);

        Assert.NotNull(projected);
        Assert.Equal(nodes.Count, projected!.Count);
        Assert.IsType<HeadingNode>(projected[0]);
        Assert.IsType<ParagraphNode>(projected[1]);
    }

    [Fact]
    public void NodesOf_WhenBlockIsNull_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => ChatBlockProjector.NodesOf(null!));
    }

    [Fact]
    public void CombineNodesPreservesDistinctBlockOrder()
    {
        var first = MarkdownRichTextParser.Parse("## First\n\nFirst text.");
        var second = MarkdownRichTextParser.Parse("## Second\n\nSecond text.");

        var projected = ChatBlockProjector.CombineNodes([WithContent(first), WithContent(second)]);

        Assert.Equal(first.Concat(second), projected);
    }

    [Fact]
    public void CombineNodesUsesLatestSnapshotOncePerBlockIdentity()
    {
        var partial = MarkdownRichTextParser.Parse("## First\n\nPartial.");
        var completed = MarkdownRichTextParser.Parse("## First\n\nPartial complete.");
        var second = MarkdownRichTextParser.Parse("## Second");
        var firstBlock = WithContent(partial);
        var secondBlock = WithContent(second);

        Assert.Equal(partial.Concat(second), ChatBlockProjector.CombineNodes([firstBlock, secondBlock]));
        SetContent(firstBlock, completed);
        var projected = ChatBlockProjector.CombineNodes([firstBlock, secondBlock, firstBlock]);

        Assert.Equal(completed.Concat(second), projected);
        Assert.DoesNotContain(partial[1], projected!);
    }

    [Fact]
    public void CombineNodesDoesNotDeduplicateIdenticalTextFromDifferentBlocks()
    {
        var nodes = MarkdownRichTextParser.Parse("## Repeated\n\nSame body.");

        var projected = ChatBlockProjector.CombineNodes([WithContent(nodes), WithContent(nodes)]);

        Assert.Equal(nodes.Concat(nodes), projected);
    }

    [Fact]
    public void CombineNodesReturnsNullForEmptyContent()
    {
        Assert.Null(ChatBlockProjector.CombineNodes([]));
        Assert.Null(ChatBlockProjector.CombineNodes([new RichContentBlock()]));
    }

    [Fact]
    public void CombineNodesKeepsRawOnlyBlocksAlongsideParsedBlocks()
    {
        var rawOnly = new RichContentBlock();
        rawOnly.AppendText("## Raw answer\n\nRaw body.");
        var parsed = MarkdownRichTextParser.Parse("## Parsed answer");

        var projected = ChatBlockProjector.CombineNodes([rawOnly, WithContent(parsed)])!;

        Assert.Equal(3, projected.Count);
        var heading = Assert.IsType<HeadingNode>(projected[0]);
        Assert.Equal("Raw answer", Assert.IsType<TextNode>(Assert.Single(heading.Children)).Text);
        Assert.IsType<ParagraphNode>(projected[1]);
        Assert.Same(parsed[0], projected[2]);
    }

    [Fact]
    public void CombineNodesRejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => ChatBlockProjector.CombineNodes(null!));
    }

    private static RichContentBlock WithContent(IReadOnlyList<RichTextNode> nodes)
    {
        var block = new RichContentBlock();
        SetContent(block, nodes);
        return block;
    }

    private static void SetContent(RichContentBlock block, IReadOnlyList<RichTextNode> nodes)
    {
        var field = typeof(RichContentBlock).GetField(
            "<Content>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(block, nodes);
    }
}

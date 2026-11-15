using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components.AI;
using System.Text;

namespace AGUIWebChat.Client.Formatting;

/// <summary>
/// Converts Markdown into the structured <see cref="RichTextNode"/> tree that
/// <see cref="RichTextContent"/> carries, so Blazor AI block renderers can draw
/// rich content without a second Markdown pass.
/// </summary>
/// <remarks>
/// The pipeline deliberately mirrors the render pipeline in <c>ChatMessage.razor</c>
/// (advanced extensions, raw HTML disabled) so the node tree and the HTML output
/// describe the same document.
/// </remarks>
public static class MarkdownRichTextParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <summary>
    /// Parses <paramref name="markdown"/> into block-level nodes. Returns an empty list
    /// for null, empty, or whitespace-only input.
    /// </summary>
    public static IReadOnlyList<RichTextNode> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        // Fully qualified: the Markdown.ColorCode package also contributes a `Markdown` namespace.
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var nodes = new List<RichTextNode>(document.Count);

        foreach (var block in document)
        {
            AppendBlock(block, nodes);
        }

        return nodes;
    }

    private static void AppendBlock(Block block, List<RichTextNode> target)
    {
        switch (block)
        {
            case HeadingBlock heading:
                {
                    var node = new HeadingNode(heading.Level);
                    AppendInlines(heading.Inline, node);
                    target.Add(node);
                    break;
                }

            case ParagraphBlock paragraph:
                {
                    var node = new ParagraphNode();
                    AppendInlines(paragraph.Inline, node);
                    target.Add(node);
                    break;
                }

            case FencedCodeBlock fenced:
                target.Add(new CodeBlockNode(ReadCode(fenced), fenced.Info ?? string.Empty));
                break;

            case CodeBlock code:
                target.Add(new CodeBlockNode(ReadCode(code), string.Empty));
                break;

            case ListBlock list:
                target.Add(CreateList(list));
                break;

            case QuoteBlock quote:
                {
                    var node = new BlockQuoteNode();
                    AddChildren(node, quote);
                    target.Add(node);
                    break;
                }

            case Table table:
                target.Add(CreateTable(table));
                break;

            case ThematicBreakBlock:
                target.Add(new ThematicBreakNode());
                break;

            // Link reference definitions carry no visible content of their own.
            case LinkReferenceDefinitionGroup:
                break;

            case ContainerBlock container:
                // Unknown container: keep the text by recursing into its children.
                foreach (var child in container)
                {
                    AppendBlock(child, target);
                }
                break;
        }
    }

    private static void AddChildren(RichTextNode parent, ContainerBlock container)
    {
        var children = new List<RichTextNode>(container.Count);

        foreach (var child in container)
        {
            AppendBlock(child, children);
        }

        foreach (var child in children)
        {
            parent.AddChild(child);
        }
    }

    private static string ReadCode(LeafBlock block)
    {
        var lines = block.Lines;
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines.Lines[i].Slice.ToString());
        }

        return builder.ToString();
    }

    private static ListNode CreateList(ListBlock list)
    {
        int? start = null;
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var parsedStart))
        {
            start = parsedStart;
        }

        var node = new ListNode(list.IsOrdered, start);

        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            var itemNode = new ListItemNode { Checked = ReadTaskListState(item) };
            AddChildren(itemNode, item);
            node.AddChild(itemNode);
        }

        return node;
    }

    /// <summary>
    /// Returns the checkbox state when the item starts with a task-list marker,
    /// otherwise <see langword="null"/>. The marker itself is dropped from the
    /// rendered content because <see cref="ListItemNode.Checked"/> carries it.
    /// </summary>
    private static bool? ReadTaskListState(ListItemBlock item)
    {
        if (item.Count == 0 || item[0] is not LeafBlock { Inline: not null } first)
        {
            return null;
        }

        return first.Inline.FirstChild is TaskList task ? task.Checked : null;
    }

    private static TableNode CreateTable(Table table)
    {
        var node = new TableNode();

        var alignments = new List<TableColumnAlignment>(table.ColumnDefinitions.Count);
        foreach (var column in table.ColumnDefinitions)
        {
            alignments.Add(column.Alignment switch
            {
                TableColumnAlign.Left => TableColumnAlignment.Left,
                TableColumnAlign.Center => TableColumnAlignment.Center,
                TableColumnAlign.Right => TableColumnAlignment.Right,
                _ => TableColumnAlignment.None
            });
        }

        node.Alignment = alignments;

        foreach (var child in table)
        {
            if (child is not TableRow row)
            {
                continue;
            }

            var rowNode = new TableRowNode();
            foreach (var cell in row)
            {
                if (cell is not TableCell tableCell)
                {
                    continue;
                }

                var cellNode = new TableCellNode();
                AppendCellContent(tableCell, cellNode);
                rowNode.AddChild(cellNode);
            }

            node.AddChild(rowNode);
        }

        return node;
    }

    private static void AppendInlines(ContainerInline? container, RichTextNode parent)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            AppendInline(inline, parent);
        }
    }

    /// <summary>
    /// Table cells hold block content (usually a single paragraph); flatten it into the
    /// cell node so renderers see inline children directly.
    /// </summary>
    private static void AppendCellContent(TableCell cell, TableCellNode target)
    {
        foreach (var block in cell)
        {
            switch (block)
            {
                case LeafBlock { Inline: not null } leaf:
                    AppendInlines(leaf.Inline, target);
                    break;

                case ContainerBlock container:
                    foreach (var child in container)
                    {
                        if (child is LeafBlock { Inline: not null } nested)
                        {
                            AppendInlines(nested.Inline, target);
                        }
                    }

                    break;
            }
        }
    }

    private static void AppendInline(Inline inline, RichTextNode parent)
    {
        switch (inline)
        {
            case LiteralInline literal:
                AppendText(parent, literal.Content.ToString());
                break;

            case EmphasisInline emphasis:
                {
                    var node = emphasis.DelimiterChar == '~'
                        ? (RichTextNode)new StrikethroughNode()
                        : emphasis.DelimiterCount >= 2
                            ? new StrongNode()
                            : new EmphasisNode();

                    AppendInlines(emphasis, node);
                    parent.AddChild(node);
                    break;
                }

            case CodeInline code:
                parent.AddChild(new InlineCodeNode(code.Content));
                break;

            case LineBreakInline lineBreak:
                parent.AddChild(lineBreak.IsHard ? new LineBreakNode() : new TextNode(" "));
                break;

            case LinkInline link when link.IsImage:
                parent.AddChild(new ImageNode(link.Url ?? string.Empty, PlainText(link), link.Title ?? string.Empty));
                break;

            case LinkInline link:
                {
                    var node = new LinkNode(link.Url ?? string.Empty, link.Title ?? string.Empty);
                    AppendInlines(link, node);
                    parent.AddChild(node);
                    break;
                }

            case AutolinkInline autolink:
                {
                    var url = autolink.Url ?? string.Empty;
                    var node = new LinkNode(url, string.Empty);
                    node.AddChild(new TextNode(url));
                    parent.AddChild(node);
                    break;
                }

            case HtmlEntityInline entity:
                AppendText(parent, entity.Transcoded.ToString());
                break;

            case HtmlInline html:
                AppendText(parent, html.Tag);
                break;

            case TaskList:
                // Consumed by ListItemNode.Checked; never rendered inline.
                break;

            case ContainerInline nested:
                AppendInlines(nested, parent);
                break;
        }
    }

    private static void AppendText(RichTextNode parent, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            parent.AddChild(new TextNode(text));
        }
    }

    /// <summary>Flattens an inline subtree to plain text (used for image alt text).</summary>
    private static string PlainText(ContainerInline container)
    {
        var builder = new StringBuilder();

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case HtmlEntityInline entity:
                    builder.Append(entity.Transcoded.ToString());
                    break;
                case ContainerInline nested:
                    builder.Append(PlainText(nested));
                    break;
            }
        }

        return builder.ToString();
    }
}

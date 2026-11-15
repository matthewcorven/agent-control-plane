using Microsoft.AspNetCore.Components.AI;

namespace AGUIWebChat.Client.Formatting
{
    /// <summary>
    /// Projects a <see cref="RichContentBlock"/> onto the client block model.
    /// </summary>
    public static class ChatBlockProjector
    {
        /// <summary>
        /// Returns the structured nodes for a block, or <see langword="null"/> when the
        /// block carries no parsed content.
        /// </summary>
        public static IReadOnlyList<RichTextNode>? NodesOf(RichContentBlock block)
        {
            ArgumentNullException.ThrowIfNull(block);

            var content = block.Content;

            return content is { Count: > 0 } ? content : null;
        }

        public static IReadOnlyList<RichTextNode>? CombineNodes(IEnumerable<RichContentBlock> blocks)
        {
            ArgumentNullException.ThrowIfNull(blocks);

            List<RichTextNode> nodes = [];
            HashSet<RichContentBlock> seen = new(ReferenceEqualityComparer.Instance);
            foreach (var block in blocks)
            {
                // Each block holds its latest cumulative snapshot, not a new message per change.
                if (!seen.Add(block)) continue;
                nodes.AddRange(NodesOf(block) ?? MarkdownRichTextParser.Parse(block.RawText));
            }

            return nodes.Count > 0 ? nodes : null;
        }
    }
}

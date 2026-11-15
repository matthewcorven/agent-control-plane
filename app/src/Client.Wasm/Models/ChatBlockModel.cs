using Microsoft.AspNetCore.Components.AI;

namespace AGUIWebChat.Client.Models
{
    /// <summary>
    /// The kinds of content block the chat transcript can render.
    /// </summary>
    public enum ChatBlockKind
    {
        RichText,
        Reasoning,
        ToolCall,
        Approval,
        UIAction
    }

    /// <summary>
    /// Lifecycle of a block as it streams in.
    /// </summary>
    public enum ChatBlockState
    {
        Pending,
        Streaming,
        Complete,
        Failed
    }

    /// <summary>
    /// A single renderable unit in the chat transcript.
    /// <para>
    /// The existing chat projects built-in rich text and observed reasoning onto
    /// this application-owned presentation model.
    /// </para>
    /// </summary>
    public sealed class ChatBlockModel
    {
        public required ChatBlockKind Kind { get; init; }

        public ChatBlockState State { get; set; } = ChatBlockState.Complete;

        /// <summary>Structured nodes for <see cref="ChatBlockKind.RichText"/>.</summary>
        public IReadOnlyList<RichTextNode>? Nodes { get; set; }

        /// <summary>Plain text fallback, also used for reasoning.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Tool name for <see cref="ChatBlockKind.ToolCall"/>.</summary>
        public string? Title { get; set; }

        /// <summary>Tool arguments or result payload.</summary>
        public string? Detail { get; set; }

        /// <summary>Duration in milliseconds, when known.</summary>
        public double? DurationMs { get; set; }

        /// <summary>Chunk count, when known.</summary>
        public int? ChunkCount { get; set; }

        /// <summary>Character count, when known.</summary>
        public int? CharacterCount { get; set; }

        /// <summary>Set when the block represents a decision the user must make.</summary>
        public bool IsResolved { get; set; }

        /// <summary>Outcome of an approval or UI action.</summary>
        public bool? Accepted { get; set; }
    }
}

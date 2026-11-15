using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text;

namespace AGUIWebChat.Client.Formatting;

/// <summary>
/// Wraps an <see cref="IChatClient"/> and attaches a parsed <see cref="RichTextContent"/>
/// to every streamed update, so the Blazor AI block pipeline renders structured
/// <see cref="RichTextNode"/> trees instead of the flat paragraph fallback.
/// </summary>
/// <remarks>
/// The shipped <c>RichTextContentHandler</c> replaces the fallback tree with the injected
/// one, so each update carries a snapshot of the whole message parsed so far. Text
/// accumulation is keyed by <see cref="ChatResponseUpdate.MessageId"/> and reset per call.
/// </remarks>
internal sealed class FormattedChatClient : DelegatingChatClient
{
    public FormattedChatClient(IChatClient inner)
        : base(inner)
    {
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Per-call state: each run is a new invocation.
        var buffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);

        await foreach (var incoming in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            var update = incoming;
            var key = update.MessageId ?? string.Empty;

            if (!buffers.TryGetValue(key, out var buffer))
            {
                buffer = new StringBuilder();
                buffers[key] = buffer;
            }

            foreach (var content in update.Contents)
            {
                if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                {
                    buffer.Append(text.Text);
                }
            }

            if (buffer.Length > 0)
            {
                var snapshot = buffer.ToString();
                var rich = new RichTextContent(snapshot, MarkdownRichTextParser.Parse(snapshot));

                if (update.Contents.IsReadOnly)
                {
                    update = CopyWith(update, rich);
                }
                else
                {
                    update.Contents.Add(rich);
                }
            }

            yield return update;
        }
    }

    private static ChatResponseUpdate CopyWith(ChatResponseUpdate source, AIContent extra)
    {
        var contents = new List<AIContent>(source.Contents.Count + 1);
        contents.AddRange(source.Contents);
        contents.Add(extra);

        return new ChatResponseUpdate(source.Role, contents)
        {
            AuthorName = source.AuthorName,
            RawRepresentation = source.RawRepresentation,
            AdditionalProperties = source.AdditionalProperties,
            ResponseId = source.ResponseId,
            MessageId = source.MessageId,
            ConversationId = source.ConversationId,
            CreatedAt = source.CreatedAt,
            FinishReason = source.FinishReason,
            ModelId = source.ModelId,
            ContinuationToken = source.ContinuationToken
        };
    }
}

using AGUI.Abstractions;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace AGUIWebChat.Client.Services;

/// <summary>
/// Wraps an <see cref="IChatClient"/> and observes each streaming run: captures the
/// AG-UI run id from <see cref="RunStartedEvent"/> raw representations and accumulates
/// reasoning text from <see cref="TextReasoningContent"/> chunks, which the shipped
/// <c>UIAgent</c> block pipeline silently drops. Reports via <see cref="ChatRunSnapshot"/>.
/// </summary>
internal sealed class ChatRunObserverClient : DelegatingChatClient
{
    private readonly Action<string> _onRunStarted;
    private readonly Action<ChatRunSnapshot> _onChanged;

    public ChatRunObserverClient(IChatClient inner, Action<string> onRunStarted, Action<ChatRunSnapshot> onChanged)
        : base(inner)
    {
        _onRunStarted = onRunStarted;
        _onChanged = onChanged;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Per-call state: each run is a new invocation.
        string? runId = null;
        var reasoning = new StringBuilder();
        var chunkCount = 0;
        var stopwatch = Stopwatch.StartNew();
        var reasoningStarted = false;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            // AGUI.Client projects RUN_ERROR as content; UIAgent does not render that content.
            if (update.RawRepresentation is RunErrorEvent runError)
            {
                throw new InvalidOperationException(runError.Message);
            }

            var changed = false;

            if (update.RawRepresentation is RunStartedEvent runStarted)
            {
                runId = runStarted.RunId;
                _onRunStarted(runId);
                changed = true;
            }

            foreach (var content in update.Contents)
            {
                if (content is TextReasoningContent trc && !string.IsNullOrEmpty(trc.Text))
                {
                    if (!reasoningStarted)
                    {
                        reasoningStarted = true;
                        stopwatch.Restart();
                    }

                    reasoning.Append(trc.Text);
                    chunkCount++;
                    changed = true;
                }
            }

            if (changed)
            {
                _onChanged(new ChatRunSnapshot(runId, reasoning.ToString(), chunkCount, stopwatch.Elapsed.TotalMilliseconds));
            }

            yield return update;
        }
    }
}

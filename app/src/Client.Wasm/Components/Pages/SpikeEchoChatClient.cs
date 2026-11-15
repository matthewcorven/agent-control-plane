using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace AGUIWebChat.Client.Components.Pages;

/// <summary>
/// Deterministic in-process <see cref="IChatClient"/> used by the #3 spike page so the
/// Blazor AI component model can be exercised without depending on a warm Ollama model.
/// Streams Markdown in small chunks so block lifecycle (streaming → complete) is observable.
/// </summary>
internal sealed class SpikeEchoChatClient : IChatClient
{
    private const string Markdown =
        "# Spike heading\n\n" +
        "This is **bold**, *italic* and `inline code`.\n\n" +
        "- item one\n" +
        "- item two\n\n" +
        "```csharp\nvar x = 1;\n```\n\n" +
        "> quoted text\n\n" +
        "| a | b |\n" +
        "|---|---|\n" +
        "| 1 | 2 |\n";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Markdown)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < Markdown.Length; i += 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string chunk = Markdown.Substring(i, Math.Min(12, Markdown.Length - i));
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Delay(15, cancellationToken);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

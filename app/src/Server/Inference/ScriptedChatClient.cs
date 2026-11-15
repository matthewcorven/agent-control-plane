using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace AGUIWebChat.Server.Inference;

/// <summary>
/// Deterministic in-process stand-in for Ollama, enabled with <c>E2E:StubInference=true</c>.
/// It echoes the last user message so E2E tests can assert on exact streamed output without
/// depending on a live model.
/// </summary>
public sealed class ScriptedChatClient(E2EChatScripts? scripts = null) : IChatClient
{
    public const string Prefix = "E2E echo: ";

    /// <summary>
    /// When the user message starts with this sentinel the stub replies with
    /// <see cref="MarkdownSample"/> instead of the echo, so E2E tests can exercise the
    /// structured rich-text renderer (headings, lists, code, tables).
    /// </summary>
    public const string MarkdownSentinel = "markdown:";

    public const string MarkdownSample = """"
        ## Markdown sample

        Some **bold** and *italic* text with `inline code`.

        - first item
        - second item

        ```csharp
        var answer = 42;
        ```

        | Name | Value |
        | ---- | ----- |
        | alpha | 1 |
        """";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = Compose(messages);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = LastUserText(messages);
        if (scripts is not null && prompt.StartsWith(E2EChatScripts.PromptPrefix, StringComparison.Ordinal))
        {
            await foreach (var update in scripts.RunAsync(
                prompt[E2EChatScripts.PromptPrefix.Length..], cancellationToken, messages))
            {
                yield return update;
            }
            yield break;
        }

        var text = Compose(messages);

        // A stable MessageId makes the AG-UI adapter aggregate the chunks into one
        // TEXT_MESSAGE instead of starting a new message per update.
        var messageId = Guid.NewGuid().ToString("N");

        // Chunked so the client exercises the same progressive-rendering path as a real model.
        foreach (var chunk in Chunk(text, 8))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Yield();

            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk)
            {
                MessageId = messageId
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static string Compose(IEnumerable<ChatMessage> messages)
    {
        var userText = LastUserText(messages);

        return userText.StartsWith(MarkdownSentinel, StringComparison.OrdinalIgnoreCase)
            ? MarkdownSample
            : Prefix + userText;
    }

    private static string LastUserText(IEnumerable<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == ChatRole.User &&
            !m.Text.StartsWith("### Current todo list\n", StringComparison.Ordinal))?.Text ?? string.Empty;

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }
}

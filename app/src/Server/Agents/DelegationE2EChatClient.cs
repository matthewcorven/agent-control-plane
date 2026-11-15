using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AGUIWebChat.Server.Agents;

// Only instantiated by the explicitly gated local E2E workflow, never by ordinary inference.
internal sealed class DelegationE2EChatClient : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var all = messages.ToArray();
        var result = all.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        bool coordinator = options?.Tools?.Any(t => t.Name == "delegate_task") == true || result is not null;
        if (coordinator && result is null)
        {
            bool block = all.Any(m => m.Text?.Contains("e2e cancellation", StringComparison.OrdinalIgnoreCase) == true);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("child-1", "delegate_task", new Dictionary<string, object?> { ["task"] = block ? "e2e cancellation" : "Investigate the real task" })])) { FinishReason = ChatFinishReason.ToolCalls };
        }
        if (coordinator) return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Coordinator received: " + result!.Result));
        if (all.Any(m => m.Text?.Contains("e2e cancellation", StringComparison.OrdinalIgnoreCase) == true)) await Task.Delay(Timeout.Infinite, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Actual specialist result"));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield break; }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

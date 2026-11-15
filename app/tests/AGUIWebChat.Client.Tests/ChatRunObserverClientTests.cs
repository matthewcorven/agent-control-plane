using AGUI.Abstractions;
using AGUIWebChat.Client.Services;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class ChatRunObserverClientTests
{
    [Fact]
    public async Task RunErrorPreservesPartialUpdatesAndSurfacesTheUpstreamMessage()
    {
        var partial = new ChatResponseUpdate(ChatRole.Assistant, "Saved partial.");
        using var inner = new ScriptedChatClient(
            partial,
            new ChatResponseUpdate(ChatRole.Assistant, [new ErrorContent("Upstream failure.")])
            {
                RawRepresentation = new RunErrorEvent { Message = "Upstream failure." }
            });
        using var observer = new ChatRunObserverClient(inner, _ => { }, _ => { });
        await using var stream = observer.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await stream.MoveNextAsync());
        Assert.Same(partial, stream.Current);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await stream.MoveNextAsync());
        Assert.Equal("Upstream failure.", error.Message);
    }

    [Fact]
    public async Task HealthyRunKeepsRunIdentityReasoningAndOriginalUpdates()
    {
        string? runId = null;
        var snapshots = new List<ChatRunSnapshot>();
        var updates = new[]
        {
            new ChatResponseUpdate { RawRepresentation = new RunStartedEvent { RunId = "run", ThreadId = "thread" } },
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking")]),
            new ChatResponseUpdate(ChatRole.Assistant, "answer")
        };
        using var inner = new ScriptedChatClient(updates);
        using var observer = new ChatRunObserverClient(inner, id => runId = id, snapshots.Add);
        var received = new List<ChatResponseUpdate>();

        await foreach (var update in observer.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            received.Add(update);
        }

        Assert.Equal(updates, received);
        Assert.Equal("run", runId);
        Assert.Equal("thinking", snapshots[^1].Reasoning);
        Assert.Equal(1, snapshots[^1].ReasoningChunkCount);
    }

    private sealed class ScriptedChatClient(params ChatResponseUpdate[] updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

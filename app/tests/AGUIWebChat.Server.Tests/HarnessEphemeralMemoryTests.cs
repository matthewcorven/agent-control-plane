using System.Runtime.CompilerServices;
using AGUIWebChat.Server.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class HarnessEphemeralMemoryTests
{
    [Fact]
    public async Task HarnessWritesThenReadsMemoryWithinRun()
    {
        using var client = new MemoryScript();
        var agent = HarnessEphemeralMemory.Create(client);
        await foreach (var _ in agent.RunStreamingAsync("Remember this", cancellationToken: TestContext.Current.CancellationToken)) { }
        Assert.Contains(client.Results, result => result.Contains("orchids", StringComparison.Ordinal));
        Assert.DoesNotContain(client.Results, result => result.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StoreIsIsolatedAndRejectsOversizedWrites()
    {
        var first = new HarnessEphemeralMemory.BoundedMemoryStore();
        var second = new HarnessEphemeralMemory.BoundedMemoryStore();
        var ct = TestContext.Current.CancellationToken;
        await first.WriteAsync("note.md", "hello", ct);
        Assert.Null(await second.ReadAsync("note.md", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WriteAsync("large.md", new string('x', 131073), ct));
        Assert.Null(await first.ReadAsync("large.md", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => first.WriteAsync("dir/../note.md", "bad", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => first.WriteAsync("dir//note.md", "bad", ct));
        Assert.Equal("hello", await first.ReadAsync("note.md", ct));
    }

    private sealed class MemoryScript : IChatClient
    {
        public List<string> Results { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var results = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
            Results.Clear();
            Results.AddRange(results.Select(r => r.Result?.ToString() ?? ""));
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Length < 2)
            {
                var args = new Dictionary<string, object?> { ["fileName"] = "note.md" };
                if (results.Length == 0) args["content"] = "orchids";
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-" + results.Length, results.Length == 0 ? "file_memory_write" : "file_memory_read", args)]) { FinishReason = ChatFinishReason.ToolCalls };
            }
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "Done.");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

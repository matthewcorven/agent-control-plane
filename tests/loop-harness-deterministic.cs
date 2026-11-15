#!/usr/bin/env dotnet
#:property DefineConstants=HARNESS_LOOP_TEST
#:include ../examples/06-loop-harness.cs
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

await Check(["wrong", "wrong", "wrong"], "iteration limit", 3, "wrong");
await Check(["wrong", "42"], "exact answer", 2, "42");
await Check(["wrong", "wrong", "42"], "iteration limit", 3, "42");
Console.WriteLine("Harness loop cap and evaluator tests passed.");

static async Task Check(string[] replies, string reason, int calls, string text)
{
    using var client = new FakeChatClient(replies);
    var result = await ArithmeticLoop.RunAsync(client);
    if (client.Calls != calls || result.Reason != reason || result.Text.Trim() != text)
        throw new Exception($"Expected {calls} calls, '{reason}', '{text}'; got {client.Calls}, '{result.Reason}', '{result.Text}'.");
}

sealed class FakeChatClient(string[] replies) : IChatClient
{
    public int Calls { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Calls >= replies.Length) throw new Exception("Unexpected extra chat invocation.");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[Calls++])));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("This test exercises the non-streaming loop.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

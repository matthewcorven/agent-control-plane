using AGUIWebChat.Server.Inference;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public sealed class ScriptedHarnessContextTests
{
    [Fact]
    public async Task TodoContextDoesNotHideScriptOrResumeItOnNextUserTurn()
    {
        using var scripts = new E2EChatScripts();
        var id = scripts.Add([new("answer", "script answer")]);
        using var client = new ScriptedChatClient(scripts);
        ChatMessage[] messages = [new(ChatRole.User, E2EChatScripts.PromptPrefix + id), new(ChatRole.User, "### Current todo list\n- none yet")];
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken)) updates.Add(update);
        Assert.Equal("script answer", Assert.Single(updates).Text);

        ChatMessage[] next = [.. messages, new(ChatRole.Assistant, "script answer"), new(ChatRole.User, "ordinary next turn"), new(ChatRole.User, "### Current todo list\n- none yet")];
        updates.Clear();
        await foreach (var update in client.GetStreamingResponseAsync(next, cancellationToken: TestContext.Current.CancellationToken)) updates.Add(update);
        Assert.Equal("E2E echo: ordinary next turn", string.Concat(updates.Select(update => update.Text)));
    }
}

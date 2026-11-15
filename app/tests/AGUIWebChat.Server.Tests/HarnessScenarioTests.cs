using System.Runtime.CompilerServices;
using AGUIWebChat.Server.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class HarnessScenarioTests
{
    [Fact]
    public async Task PlanningExposesHarnessToolsWithoutFileOrWebTools()
    {
        using var client = new RecordingClient("done");
        var agent = HarnessScenarios.Create("/ag-ui/harness-planning", client);
        await foreach (var _ in agent.RunStreamingAsync("Plan a task", cancellationToken: TestContext.Current.CancellationToken)) { }
        var tools = Assert.Single(client.SeenTools);
        Assert.Contains(tools, name => name.Contains("todo", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tools, name => name.Contains("file", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tools, name => name.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SkillsLoadsBundledInstructionsWithoutFileTools()
    {
        using var client = new SkillScript();
        var agent = HarnessScenarios.Create("/ag-ui/harness-skills", client);
        await foreach (var _ in agent.RunStreamingAsync("Summarize the meeting", cancellationToken: TestContext.Current.CancellationToken)) { }
        Assert.Contains("action items", client.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(client.Tools, name => name.Contains("file", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("load_skill", client.Tools);
    }

    [Theory]
    [InlineData("42", 1)]
    [InlineData(" 42 ", 1)]
    [InlineData("42!", 3)]
    [InlineData("wrong", 3)]
    public async Task LoopStopsOnExactAnswerOrIterationLimit(string answer, int expectedCalls)
    {
        using var client = new RecordingClient(answer);
        var agent = HarnessScenarios.Create("/ag-ui/harness-loop", client);
        await foreach (var _ in agent.RunStreamingAsync("start", cancellationToken: TestContext.Current.CancellationToken)) { }
        Assert.Equal(expectedCalls, client.SeenTools.Count);
    }

    private sealed class SkillScript : IChatClient
    {
        public string Result { get; private set; } = "";
        public string[] Tools { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Tools = options?.Tools?.Select(t => t.Name).ToArray() ?? [];
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault(r => r.CallId == "skill-1");
            await Task.Yield();
            if (result is null)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("skill-1", "load_skill", new Dictionary<string, object?> { ["skillName"] = "meeting-summary" })]) { FinishReason = ChatFinishReason.ToolCalls };
            else
            {
                Result = result.Result?.ToString() ?? result.Exception?.Message ?? "";
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Done.");
            }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingClient(string answer) : IChatClient
    {
        public List<string[]> SeenTools { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SeenTools.Add(options?.Tools?.Select(t => t.Name).ToArray() ?? []);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, answer);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

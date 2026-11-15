using System.Runtime.CompilerServices;
using AGUIWebChat.Server.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class HarnessAssistantTests
{
    [Fact]
    public async Task AssistantExposesPlanningAndAwaitsRealMealHelperWithoutFileAccess()
    {
        using var client = new MealScript();
        var agent = HarnessAssistant.Create(client);
        await foreach (var _ in agent.RunStreamingAsync("Plan dinner", cancellationToken: TestContext.Current.CancellationToken)) { }

        Assert.Contains("meal_ideas", client.Tools);
        Assert.Contains(client.Tools, name => name.Contains("todo", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(client.Tools, name => name.Contains("file", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(client.Tools, name => name.Contains("background", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, client.HelperCalls);
        Assert.Contains("lentil soup", client.ToolResult, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MealScript : IChatClient
    {
        public string[] Tools { get; private set; } = [];
        public int HelperCalls { get; private set; }
        public string ToolResult { get; private set; } = "";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HelperCalls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Lentil soup")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Tools = options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
            var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault(content => content.CallId == "meal-1");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("meal-1", "meal_ideas", new Dictionary<string, object?> { ["request"] = "easy dinner" })]) { FinishReason = ChatFinishReason.ToolCalls };
            else
            {
                ToolResult = result.Result?.ToString() ?? result.Exception?.Message ?? "";
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Dinner is ready to plan.");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

#!/usr/bin/env dotnet
#:include common.cs
#:package Microsoft.Agents.AI.Harness@1.28.0
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

#pragma warning disable MAAI001 // Harness looping APIs are experimental in 1.28.0.
#if !HARNESS_LOOP_TEST
var endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
using var http = LocalHttp.Create(endpoint);
var client = new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions
{
    Endpoint = endpoint, Transport = new HttpClientPipelineTransport(http)
});
var result = await ArithmeticLoop.RunAsync(client.GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient());
Console.WriteLine(result.Text);
Console.WriteLine($"Stop reason: {result.Reason}");
return result.Reason == "exact answer" ? 0 : 1;
#endif

static class ArithmeticLoop
{
    public const int MaxIterations = 3;

    public static async Task<(string Text, string Reason)> RunAsync(IChatClient chatClient)
    {
        // Check the actual answer, not the model's claim of completion.
        var stoppedByEvaluator = false;
        var evaluator = new DelegateLoopEvaluator((context, _) =>
        {
            stoppedByEvaluator = string.Equals(context.LastResponse.Text.Trim(), "42", StringComparison.Ordinal);
            return ValueTask.FromResult(stoppedByEvaluator ? LoopEvaluation.Stop() :
                LoopEvaluation.Continue("The answer was not exactly 42. Try again; output only the integer."));
        });
        var agent = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            DisableAgentSkillsProvider = true, DisableTodoProvider = true, DisableAgentModeProvider = true,
            DisableFileMemory = true, DisableWebSearch = true, DisableToolAutoApproval = true, DisableOpenTelemetry = true,
            LoopEvaluators = [evaluator], LoopAgentOptions = new LoopAgentOptions { MaxIterations = MaxIterations, NonStreamingReturnsLastResponseOnly = true },
            ChatOptions = new ChatOptions { Instructions = "Answer the arithmetic question with only the integer." }
        });
        var response = await agent.RunAsync("What is 6 multiplied by 7?");
        // The safety cap may stop before the evaluator sees the final response.
        return (response.Text, stoppedByEvaluator ? "exact answer" : "iteration limit");
    }
}

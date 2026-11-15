#!/usr/bin/env dotnet
#:package Microsoft.Agents.AI.Harness@1.28.0
#:property PublishAot=false
#:include common.cs
#pragma warning disable MAAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

var endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
using var http = LocalHttp.Create(endpoint);
http.Timeout = TimeSpan.FromSeconds(90);
var chat = new OpenAIClient(new ApiKeyCredential("local-no-auth"),
    new OpenAIClientOptions { Endpoint = endpoint, Transport = new HttpClientPipelineTransport(http) })
    .GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient();

var agent = chat.AsHarnessAgent(new HarnessAgentOptions
{
    Name = "Planner",
    DisableFileMemory = true, DisableAgentSkillsProvider = true,
    DisableWebSearch = true, DisableToolAutoApproval = true, DisableOpenTelemetry = true,
    MaximumIterationsPerRequest = 8,
    AgentModeProviderOptions = new AgentModeProviderOptions { DefaultMode = "plan" },
    LoopEvaluators = [new TodoCompletionLoopEvaluator(new TodoCompletionLoopEvaluatorOptions { Modes = ["execute"] })],
    LoopAgentOptions = new LoopAgentOptions { MaxIterations = 4 },
    ChatOptions = new ChatOptions
    {
        MaxOutputTokens = 1024,
        Instructions = "Help plan a task using the todo tools. Keep the plan and progress in the session, not files. Ask before switching to execute mode. Execution means drafting text only; you have no external action tools. Do not claim unperformed work was done."
    }
});
var session = await agent.CreateSessionAsync();
Console.WriteLine("Planning harness: describe a task, then explicitly ask to execute it. Type exit to quit.");
Console.WriteLine("Plan/execute confirmation is model instruction, not an authorization boundary. State lasts only for this process.");
for (var turn = 0; turn < 20; turn++)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Length > 4000) { Console.WriteLine("Input is limited to 4000 characters."); continue; }
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        await foreach (var update in agent.RunStreamingAsync(input, session, cancellationToken: timeout.Token))
        {
            foreach (var call in update.Contents.OfType<FunctionCallContent>())
                Console.Error.WriteLine($"[Harness tool call: {call.Name}]");
            Console.Write(update);
        }
        Console.WriteLine();
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        Console.Error.WriteLine("Turn timed out; ending the session.");
        break;
    }
}

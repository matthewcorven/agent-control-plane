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

HarnessAgent Create(string name, string description, string instructions, IList<AIAgent>? children = null) =>
    chat.AsHarnessAgent(new HarnessAgentOptions
    {
        Name = name, Description = description,
        DisableFileMemory = true, DisableAgentSkillsProvider = true,
        DisableWebSearch = true, DisableToolAutoApproval = true, DisableOpenTelemetry = true,
        DisableTodoProvider = true, DisableAgentModeProvider = true,
        MaximumIterationsPerRequest = 8,
        BackgroundAgents = children,
        LoopEvaluators = children is null ? null : [new BackgroundTaskCompletionLoopEvaluator()],
        LoopAgentOptions = new LoopAgentOptions { MaxIterations = 4 },
        ChatOptions = new ChatOptions { Instructions = instructions, MaxOutputTokens = 1024 }
    });

var recipes = Create("RecipePlanner", "Suggests simple meal ideas and ingredients.",
    "Suggest practical meal ideas using only the supplied constraints. Do not assume access to recipes or the web.");
var groceries = Create("GroceryPlanner", "Suggests a practical grocery list.",
    "Suggest a concise grocery list for the supplied meal requirements. State assumptions; do not claim to have checked prices.");
var coordinator = Create("Coordinator", "Coordinates a meal plan.",
    "Start a background task with RecipePlanner and another with GroceryPlanner before waiting. " +
    "Wait for both, retrieve their results, and use them to draft a coherent meal plan and grocery list. " +
    "Report failures or missing results honestly; never pretend you delegated or checked external sources. " +
    "Clear completed tasks after retrieving their results.", [recipes, groceries]);

var request = args.Length == 0
    ? "Plan three inexpensive vegetarian dinners for two people, with a consolidated grocery list."
    : string.Join(' ', args);
if (request.Length > 4000) throw new ArgumentException("Request is limited to 4000 characters.");
Console.WriteLine("Meal planning with two in-process background agents; model tool use is not guaranteed.");
var session = await coordinator.CreateSessionAsync();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
try
{
    await foreach (var update in coordinator.RunStreamingAsync(request, session, cancellationToken: timeout.Token))
    {
        foreach (var call in update.Contents.OfType<FunctionCallContent>())
            Console.Error.WriteLine($"[Harness tool call: {call.Name}]");
        Console.Write(update);
    }
    Console.WriteLine();
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
    Console.Error.WriteLine("Meal planning timed out; background task completion is not guaranteed.");
    Environment.ExitCode = 1;
}

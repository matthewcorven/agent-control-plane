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

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet examples/07-assistant-harness.cs -- <existing-workspace-directory>");
    return;
}

var workspace = Path.GetFullPath(args[0]);
Console.WriteLine($"Workspace: {workspace}\nFile reads and writes require approval. This is not a sandbox. Type exit to quit.");
var endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
using var http = LocalHttp.Create(endpoint);
var client = new OpenAIClient(new ApiKeyCredential("local-no-auth"),
    new OpenAIClientOptions { Endpoint = endpoint, Transport = new HttpClientPipelineTransport(http) })
    .GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient();
AIAgent helper = client.AsHarnessAgent(new HarnessAgentOptions
{
    Name = "MealHelper", Description = "Suggests practical meal ideas.",
    DisableTodoProvider = true, DisableAgentModeProvider = true, DisableFileMemory = true,
    DisableAgentSkillsProvider = true, DisableWebSearch = true, DisableToolAutoApproval = true, DisableOpenTelemetry = true,
    ChatOptions = new ChatOptions { Instructions = "Suggest concise meal ideas. You have no file access." }
});
AIAgent assistant = client.AsHarnessAgent(new HarnessAgentOptions
{
    Name = "HouseholdAssistant", BackgroundAgents = [helper],
    FileAccessStore = new FileSystemAgentFileStore(workspace),
    DisableFileMemory = true, DisableAgentSkillsProvider = true, DisableWebSearch = true,
    DisableToolAutoApproval = true, DisableOpenTelemetry = true,
    ChatOptions = new ChatOptions { Instructions = "Help with household tasks. Use planning and todos for multi-step work. Delegate meal ideas to MealHelper when useful. Ask before file access; never claim an unapproved action happened." }
});
var session = await assistant.CreateSessionAsync();
while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
    if (string.IsNullOrWhiteSpace(input)) continue;
    ChatMessage message = new(ChatRole.User, input);
    while (true)
    {
        var approvals = new List<ToolApprovalRequestContent>();
        await foreach (var update in assistant.RunStreamingAsync(message, session))
        {
            Console.Write(update.Text);
            approvals.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());
        }
        Console.WriteLine();
        if (approvals.Count == 0) break;
        var responses = new List<AIContent>();
        foreach (var request in approvals)
        {
            var call = request.ToolCall as FunctionCallContent;
            Console.WriteLine($"Approval requested: {call?.Name} {System.Text.Json.JsonSerializer.Serialize(call?.Arguments)}");
            Console.Write("Allow? Type yes to approve: ");
            responses.Add(request.CreateResponse(string.Equals(Console.ReadLine(), "yes", StringComparison.OrdinalIgnoreCase)));
        }
        message = new ChatMessage(ChatRole.User, responses);
    }
}

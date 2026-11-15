#!/usr/bin/env dotnet
#:package Microsoft.Agents.AI.Harness@1.28.0
#:property PublishAot=false
#:include common.cs
#pragma warning disable MAAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run examples/03-files-harness.cs -- <existing-workspace>");
    return;
}
var root = Path.GetFullPath(args[0]);
Console.WriteLine($"Workspace: {root}\nFile-store scoping is not an OS sandbox. Use a trusted, private workspace; do not put secrets in it. Each file operation requires approval. No shell or web tools are enabled.");
var endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
using var http = LocalHttp.Create(endpoint);
var client = new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions { Endpoint = endpoint, Transport = new HttpClientPipelineTransport(http) });
var agent = client.GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient().AsHarnessAgent(new HarnessAgentOptions
{
    DisableTodoProvider = true, DisableAgentModeProvider = true, DisableFileMemory = true,
    DisableAgentSkillsProvider = true, DisableWebSearch = true, DisableToolAutoApproval = true,
    DisableOpenTelemetry = true, FileAccessStore = new FileSystemAgentFileStore(root),
    ChatOptions = new ChatOptions { Instructions = "Use only the provided file tools. Never request or store secrets." }
});
var session = await agent.CreateSessionAsync();
while (true)
{
    Console.Write("You (empty to exit): ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) break;
    var response = await agent.RunAsync(line, session);
    while (true)
    {
        if (!string.IsNullOrWhiteSpace(response.Text)) Console.WriteLine(response.Text);
        var requests = response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToArray();
        if (requests.Length == 0) break;
        var decisions = new List<AIContent>();
        foreach (var request in requests)
        {
            var call = request.ToolCall as FunctionCallContent;
            Console.WriteLine($"\nRequested: {call?.Name ?? "unknown"}\nArguments: {JsonSerializer.Serialize(call?.Arguments, new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() })}");
            Console.Write("Approve this exact operation? Type YES: ");
            var approved = call is not null && !Console.IsInputRedirected && Console.ReadLine() == "YES";
            decisions.Add(request.CreateResponse(approved));
        }
        response = await agent.RunAsync(new ChatMessage(ChatRole.User, decisions), session);
    }
}

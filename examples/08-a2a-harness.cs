#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Agents.AI.Harness@1.28.0
#:package Microsoft.Agents.AI.A2A@1.28.0-preview.261106.1
#:package Microsoft.Agents.AI.Hosting.A2A.AspNetCore@1.28.0-preview.261106.1
#:property PublishAot=false
#:include common.cs
#pragma warning disable MAAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using A2A;
using A2A.AspNetCore;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;

// dotnet run examples/08-a2a-harness.cs [--model]
// Default is a deterministic SDK handler. --model runs a real Harness agent over A2A streaming.
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
using var modelHttp = LocalHttp.Create(LocalEndpoint.Create("http://127.0.0.1:8001/v1"));
bool model = args.SequenceEqual(["--model"]);
if (args.Length != 0 && !model) throw new ArgumentException("Usage: [--model]");
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
if (model)
{
    var endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
    var chat = new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions
    { Endpoint = endpoint, Transport = new HttpClientPipelineTransport(modelHttp) })
        .GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient();
    AIAgent agent = chat.AsHarnessAgent(new HarnessAgentOptions
    {
        Name = "LocalAssistant", DisableTodoProvider = true, DisableAgentModeProvider = true,
        DisableFileMemory = true, DisableAgentSkillsProvider = true, DisableWebSearch = true,
        DisableToolAutoApproval = true, ChatOptions = new ChatOptions { Instructions = "Answer briefly." }
    });
    builder.Services.AddKeyedSingleton<AIAgent>("demo", agent);
}
if (model) builder.AddA2AServer("demo");
else builder.Services.AddKeyedSingleton<A2AServer>("demo", new A2AServer(new DemoHandler(), new InMemoryTaskStore(), new ChannelEventNotifier(), Microsoft.Extensions.Logging.Abstractions.NullLogger<A2AServer>.Instance, new A2AServerOptions()));
await using var app = builder.Build();
app.MapA2AHttpJson("demo", "/a2a");
AgentCard? card = null;
app.MapGet("/.well-known/agent-card.json", () => Microsoft.AspNetCore.Http.Results.Json(card, global::A2A.A2AJsonUtilities.DefaultOptions));
await app.StartAsync(timeout.Token);
try
{
    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    card = new AgentCard { Name = "Local demo", Description = "Loopback-only A2A demo", Version = "1.0",
        SupportedInterfaces = [new AgentInterface { Url = address + "/a2a", ProtocolBinding = ProtocolBindingNames.HttpJson, ProtocolVersion = "1.0" }] };
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    var discovered = await new A2ACardResolver(new Uri(address), http).GetAgentCardAsync(timeout.Token);
    if (discovered.SupportedInterfaces.Count != 1 || discovered.SupportedInterfaces[0].Url != address + "/a2a" || discovered.SupportedInterfaces[0].ProtocolBinding != ProtocolBindingNames.HttpJson)
        throw new InvalidOperationException("Unexpected agent card");
    Console.WriteLine("Discovered: " + discovered.Name + " (HTTP+JSON)");
    var client = A2AClientFactory.Create(discovered, http, new A2AClientOptions { PreferredBindings = [ProtocolBindingNames.HttpJson] });
    var request = Request("hello");
    bool sawTask = false, sawArtifact = false, sawCompleted = false, sawText = false;
    await foreach (var item in client.SendStreamingMessageAsync(request, timeout.Token))
    {
        Console.WriteLine("Stream: " + item.PayloadCase + (item.StatusUpdate is null ? "" : " " + item.StatusUpdate.Status.State));
        if (item.Task is not null && item.Task.Status.State == TaskState.Working) sawTask = true;
        if (item.ArtifactUpdate?.Artifact.Parts.Any(p => p.Text == "finished: hello") == true) sawArtifact = true;
        if (item.StatusUpdate?.Status.State == TaskState.Completed) sawCompleted = true;
        if (item.Message is not null)
        {
            var text = string.Join(" ", item.Message.Parts.Select(p => p.Text));
            sawText |= !string.IsNullOrWhiteSpace(text);
            Console.WriteLine(text);
        }
        if (model && item.ArtifactUpdate?.Artifact.Parts.Any(p => !string.IsNullOrWhiteSpace(p.Text)) == true) sawText = true;
    }
    if (model ? !sawText : !(sawTask && sawArtifact && sawCompleted))
        throw new InvalidOperationException("Incomplete streaming response");
    if (!model)
    {
        var background = Request("background");
        background.Configuration = new SendMessageConfiguration { ReturnImmediately = true };
        var started = await client.SendMessageAsync(background, timeout.Token);
        string id = started.Task?.Id ?? throw new InvalidOperationException("Expected task");
        Console.WriteLine("Background task: " + id + " " + started.Task!.Status.State);
        if (started.Task.Status.State != TaskState.Working) throw new InvalidOperationException("Expected a running background task");
        bool sawWorking = false;
        for (int i = 0; i < 50; i++)
        {
            var task = await client.GetTaskAsync(new GetTaskRequest { Id = id }, timeout.Token);
            Console.WriteLine("Poll: " + task.Status.State);
            sawWorking |= task.Status.State == TaskState.Working;
            if (task.Status.State == TaskState.Completed)
            {
                if (!sawWorking) throw new InvalidOperationException("No running task observed during polling");
                if (task.Artifacts?.SelectMany(a => a.Parts).Any(p => p.Text == "finished: background") != true)
                    throw new InvalidOperationException("Missing background result");
                Console.WriteLine("Background result verified");
                return;
            }
            if (task.Status.State is TaskState.Failed or TaskState.Canceled) throw new InvalidOperationException("Task failed");
            await Task.Delay(100, timeout.Token);
        }
        throw new TimeoutException("Task did not complete");
    }
}
finally { using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(shutdown.Token); }

static SendMessageRequest Request(string text) => new() { Message = new Message { MessageId = Guid.NewGuid().ToString(), Role = Role.User, Parts = [Part.FromText(text)] } };

sealed class DemoHandler : IAgentHandler
{
    public async Task ExecuteAsync(RequestContext context, AgentEventQueue queue, CancellationToken cancellationToken)
    {
        await queue.EnqueueTaskAsync(new AgentTask { Id = context.TaskId, ContextId = context.ContextId, Status = new global::A2A.TaskStatus { State = TaskState.Working } }, cancellationToken);
        await Task.Delay(500, cancellationToken);
        await queue.EnqueueArtifactUpdateAsync(new TaskArtifactUpdateEvent { TaskId = context.TaskId, ContextId = context.ContextId, Artifact = new Artifact { ArtifactId = "result", Parts = [Part.FromText("finished: " + context.UserText)] }, LastChunk = true }, cancellationToken);
        await queue.EnqueueStatusUpdateAsync(new TaskStatusUpdateEvent { TaskId = context.TaskId, ContextId = context.ContextId, Status = new global::A2A.TaskStatus { State = TaskState.Completed } }, cancellationToken);
        queue.Complete();
    }
    public Task CancelAsync(RequestContext context, AgentEventQueue queue, CancellationToken cancellationToken) { queue.Complete(); return Task.CompletedTask; }
}

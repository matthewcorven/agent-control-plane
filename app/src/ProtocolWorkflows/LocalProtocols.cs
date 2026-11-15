using System.ComponentModel;
using System.IO.Pipelines;
using System.Net;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ProtocolWorkflows;

public sealed record ProtocolResult(string[] Events);

public static class LocalProtocols
{
    public static async Task<ProtocolResult> A2AAsync(string message, string mode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 1000 || mode is not ("stream" or "background")) throw new ArgumentException("Invalid request.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var server = new A2AServer(new EchoHandler(), new InMemoryTaskStore(), new ChannelEventNotifier(), Microsoft.Extensions.Logging.Abstractions.NullLogger<A2AServer>.Instance, new A2AServerOptions());
        await using var app = builder.Build();
        AgentCard? card = null;
        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress) || context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
            await next();
        });
        app.MapHttpA2A(server, "/a2a");
        app.MapGet("/.well-known/agent-card.json", () => Microsoft.AspNetCore.Http.Results.Json(card, A2AJsonUtilities.DefaultOptions));
        await app.StartAsync(timeout.Token);
        try
        {
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            card = new AgentCard { Name = "Local echo demo", Description = "Deterministic local protocol example", Version = "1.0", SupportedInterfaces = [new AgentInterface { Url = address + "/a2a", ProtocolBinding = ProtocolBindingNames.HttpJson, ProtocolVersion = "1.0" }] };
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 65536 };
            var discovered = await new A2ACardResolver(new Uri(address), http).GetAgentCardAsync(timeout.Token);
            if (discovered.SupportedInterfaces.Count != 1 || discovered.SupportedInterfaces[0].Url != address + "/a2a" || discovered.SupportedInterfaces[0].ProtocolBinding != ProtocolBindingNames.HttpJson) throw new InvalidOperationException("Unexpected card.");
            var client = A2AClientFactory.Create(discovered, http, new A2AClientOptions { PreferredBindings = [ProtocolBindingNames.HttpJson] });
            var request = new SendMessageRequest { Message = new Message { MessageId = Guid.NewGuid().ToString(), Role = A2A.Role.User, Parts = [Part.FromText(message)] } };
            var events = new List<string> { "Discovered: " + discovered.Name + " (HTTP+JSON)" };
            if (mode == "stream")
            {
                await foreach (var item in client.SendStreamingMessageAsync(request, timeout.Token))
                {
                    if (events.Count >= 50) throw new InvalidOperationException("Too many events.");
                    if (item.Task is not null) events.Add("Task: " + item.Task.Status.State);
                    if (item.ArtifactUpdate is not null) events.Add("Artifact: " + string.Join(" ", item.ArtifactUpdate.Artifact.Parts.Select(p => p.Text)));
                    if (item.StatusUpdate is not null) events.Add("Status: " + item.StatusUpdate.Status.State);
                }
            }
            else
            {
                request.Configuration = new SendMessageConfiguration { ReturnImmediately = true };
                var started = await client.SendMessageAsync(request, timeout.Token);
                var task = started.Task ?? throw new InvalidOperationException("No task returned.");
                events.Add("Started: " + task.Status.State);
                for (int i = 0; i < 50; i++)
                {
                    task = await client.GetTaskAsync(new GetTaskRequest { Id = task.Id }, timeout.Token);
                    if (task.Status.State == TaskState.Completed) { events.Add("Completed: " + string.Join(" ", task.Artifacts?.SelectMany(a => a.Parts).Select(p => p.Text) ?? [])); return new(events.ToArray()); }
                    if (task.Status.State is TaskState.Failed or TaskState.Canceled) throw new InvalidOperationException("Task failed.");
                    await Task.Delay(100, timeout.Token);
                }
                throw new TimeoutException();
            }
            return new(events.ToArray());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    public static async Task<ProtocolResult> McpAsync(int left, int right, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var incoming = new Pipe();
        var outgoing = new Pipe();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithStreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream()).WithTools<ArithmeticTools>();
        using var host = builder.Build();
        await host.StartAsync(timeout.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()), cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            var result = await client.CallToolAsync("add_numbers", new Dictionary<string, object?> { ["left"] = left, ["right"] = right }, cancellationToken: timeout.Token);
            if (result.IsError == true) throw new InvalidOperationException("MCP tool failed.");
            return new(["Tools: " + string.Join(", ", tools.Select(t => t.Name)), "Result: " + string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text))]);
        }
        finally { await host.StopAsync(CancellationToken.None); }
    }

    [McpServerToolType]
    public sealed class ArithmeticTools
    {
        [McpServerTool(Name = "add_numbers"), Description("Add two integers.")]
        public static string Add(int left, int right) => checked(left + right).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class EchoHandler : IAgentHandler
    {
        public async Task ExecuteAsync(RequestContext context, AgentEventQueue queue, CancellationToken cancellationToken)
        {
            await queue.EnqueueTaskAsync(new AgentTask { Id = context.TaskId, ContextId = context.ContextId, Status = new global::A2A.TaskStatus { State = TaskState.Working } }, cancellationToken);
            await Task.Delay(500, cancellationToken);
            await queue.EnqueueArtifactUpdateAsync(new TaskArtifactUpdateEvent { TaskId = context.TaskId, ContextId = context.ContextId, Artifact = new Artifact { ArtifactId = "result", Parts = [Part.FromText("Echo: " + context.UserText)] }, LastChunk = true }, cancellationToken);
            await queue.EnqueueStatusUpdateAsync(new TaskStatusUpdateEvent { TaskId = context.TaskId, ContextId = context.ContextId, Status = new global::A2A.TaskStatus { State = TaskState.Completed } }, cancellationToken);
            queue.Complete();
        }
        public Task CancelAsync(RequestContext context, AgentEventQueue queue, CancellationToken cancellationToken) { queue.Complete(); return Task.CompletedTask; }
    }
}

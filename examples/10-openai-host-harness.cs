#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Agents.AI.Harness@1.28.0
#:package Microsoft.Agents.AI.Hosting.OpenAI@1.28.0-alpha.261106.1
#:property PublishAot=false
#:include common.cs
#pragma warning disable MAAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using OpenAI;

// Run: dotnet run examples/10-openai-host-harness.cs
// Local, unauthenticated demonstration only. Stop with Ctrl+C. Do not expose through a proxy.
var endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
using var http = LocalHttp.Create(endpoint);
http.Timeout = TimeSpan.FromSeconds(90);
var chat = new OpenAIClient(new ApiKeyCredential("local-no-auth"),
    new OpenAIClientOptions { Endpoint = endpoint, Transport = new HttpClientPipelineTransport(http) })
    .GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient();
var agent = chat.AsHarnessAgent(new HarnessAgentOptions
{
    Name = "harness",
    DisableFileMemory = true, DisableAgentSkillsProvider = true,
    DisableWebSearch = true, DisableToolAutoApproval = true, DisableOpenTelemetry = true,
    MaximumIterationsPerRequest = 4,
    ChatOptions = new ChatOptions { MaxOutputTokens = 512, Instructions = "Answer briefly and helpfully." }
});

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 5079));
builder.AddOpenAIChatCompletions();
builder.AddOpenAIResponses();
var hostedAgent = builder.AddAIAgent("harness", (_, _) => agent);
var app = builder.Build();
app.Use(async (context, next) =>
{
    // Prevent browser DNS rebinding to the unauthenticated loopback service.
    if (!string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    await next(context);
});
app.MapOpenAIChatCompletions(hostedAgent, path: "/v1/chat/completions");
app.MapOpenAIResponses(hostedAgent, path: "/v1/responses");
Console.WriteLine("Unauthenticated local demo: http://127.0.0.1:5079 (Ctrl+C to stop)");
await app.RunAsync();

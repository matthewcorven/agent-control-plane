using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAIHost;

if (!HostWorkflow.IsEnabled(args, Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")))
{
    Console.Error.WriteLine("Requires DOTNET_ENVIRONMENT=Development and --development-loopback. This unauthenticated host is for local development only.");
    return 1;
}

using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
var chat = new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions
{
    Endpoint = new Uri("http://127.0.0.1:8001/v1"), Transport = new HttpClientPipelineTransport(http)
}).GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient();
await using var app = HostWorkflow.Create(chat, 5079);
Console.WriteLine("Unauthenticated loopback-only development host: http://127.0.0.1:5079. Do not proxy or expose it.");
await app.RunAsync();
return 0;

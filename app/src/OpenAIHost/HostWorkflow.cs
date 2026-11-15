using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;

namespace OpenAIHost;

public static class HostWorkflow
{
    public static bool IsEnabled(string[] args, string? environment) =>
        args.Length == 1 && args[0] == "--development-loopback" &&
        string.Equals(environment, "Development", StringComparison.Ordinal);

    public static WebApplication Create(IChatClient client, int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.AddOpenAIChatCompletions();
        builder.AddOpenAIResponses();
        var agent = client.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "harness", DisableFileMemory = true, DisableAgentSkillsProvider = true,
            DisableWebSearch = true, DisableToolAutoApproval = true, DisableOpenTelemetry = true,
            MaximumIterationsPerRequest = 4,
            ChatOptions = new ChatOptions { MaxOutputTokens = 512, Instructions = "Answer briefly and helpfully." }
        });
        var hosted = builder.AddAIAgent("harness", (_, _) => agent);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote) ||
                !(string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)) ||
                context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.MapOpenAIChatCompletions(hosted, path: "/v1/chat/completions");
        app.MapOpenAIResponses(hosted, path: "/v1/responses");
        return app;
    }
}

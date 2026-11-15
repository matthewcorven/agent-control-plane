#!/usr/bin/env dotnet
#:package Microsoft.Agents.AI.Harness@1.28.0
#:package ModelContextProtocol@1.4.1
#:package Microsoft.Extensions.Hosting@10.0.0
#:property PublishAot=false
#:include common.cs
#pragma warning disable MAAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenAI;

// From the template root: dotnet run examples/09-mcp-harness.cs -- --self-test
// For a model-driven turn: dotnet run examples/09-mcp-harness.cs -- --model
// The self-test requires no model. The optional model mode uses only the loopback endpoint in common.cs.
if (args is ["--server"])
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders(); // stdout belongs exclusively to MCP
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<LocalTools>();
    await builder.Build().RunAsync();
    return;
}
if (args is not ["--self-test"] and not ["--model"])
{
    Console.Error.WriteLine("Usage: dotnet run examples/09-mcp-harness.cs -- --self-test|--model");
    return;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate this executable.");
var dll = typeof(LocalTools).Assembly.Location;
var isDotnetHost = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "local-arithmetic",
    Command = executable,
    Arguments = isDotnetHost ? [dll, "--server"] : ["--server"],
    InheritEnvironmentVariables = false,
});
await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
var tools = await mcp.ListToolsAsync(cancellationToken: timeout.Token);
var add = tools.Single(t => t.Name == "add_numbers");
Console.WriteLine($"MCP tools: {string.Join(", ", tools.Select(t => t.Name))}");
var direct = await mcp.CallToolAsync("add_numbers", new Dictionary<string, object?> { ["left"] = 19, ["right"] = 23 }, cancellationToken: timeout.Token);
if (direct.IsError == true || !direct.Content.OfType<TextContentBlock>().Any(c => c.Text == "42"))
    throw new InvalidOperationException("Direct MCP call failed.");
Console.WriteLine("Direct MCP call: 19 + 23 = 42");

using var http = LocalHttp.Create(LocalEndpoint.Create("http://127.0.0.1:8001/v1"));
using IChatClient chat = args[0] == "--self-test" ? new ScriptedChatClient() :
    new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions
    {
        Endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1"),
        Transport = new HttpClientPipelineTransport(http)
    }).GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient();
var agent = chat.AsHarnessAgent(new HarnessAgentOptions
{
    Name = "ArithmeticAssistant",
    DisableTodoProvider = true, DisableAgentModeProvider = true, DisableFileMemory = true,
    DisableAgentSkillsProvider = true, DisableWebSearch = true, DisableToolAutoApproval = true,
    DisableOpenTelemetry = true, MaximumIterationsPerRequest = 4,
    ChatOptions = new ChatOptions { Tools = [add], MaxOutputTokens = 512,
        Instructions = "Use add_numbers to calculate the requested sum. Report its result." }
});
var response = await agent.RunAsync("Use add_numbers to add 19 and 23.", cancellationToken: timeout.Token);
Console.WriteLine($"Harness response: {response.Text}");
var observedCalls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count(c => c.Name == "add_numbers");
Console.WriteLine($"MCP tool calls observed in Harness response: {observedCalls}");
if (chat is ScriptedChatClient scripted && (!scripted.SawToolResult || !response.Text.Contains("42", StringComparison.Ordinal)))
    throw new InvalidOperationException("Harness did not execute the MCP tool and return its result.");
if (args[0] == "--model") Console.WriteLine("Model-directed tool selection is probabilistic; inspect the response, not just its exit code.");

[McpServerToolType]
sealed class LocalTools
{
    [McpServerTool(Name = "add_numbers"), Description("Add two integers and return their sum.")]
    public static string Add(int left, int right) => checked(left + right).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

sealed class ScriptedChatClient : IChatClient
{
    private int _calls;
    public bool SawToolResult { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_calls++ == 0)
        {
            if (options?.Tools?.Any(t => t.Name == "add_numbers") != true) throw new InvalidOperationException("MCP tool missing from Harness chat options.");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "add_numbers", new Dictionary<string, object?> { ["left"] = 19, ["right"] = 23 })])));
        }
        SawToolResult = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Any(r => r.CallId == "call-1" && r.Result?.ToString()?.Contains("42", StringComparison.Ordinal) == true);
        if (!SawToolResult) throw new InvalidOperationException("MCP result missing from model conversation.");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "42")));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new NotSupportedException();
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

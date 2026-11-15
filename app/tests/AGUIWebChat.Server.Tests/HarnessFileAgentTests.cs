using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AGUIWebChat.Server.Agents;
using AGUIWebChat.Server.Inference;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class HarnessFileAgentTests
{
    [Fact]
    public async Task HarnessModelReadsOnlySelectedContentThroughTool()
    {
        using var client = new ScriptClient();
        string output = await HarnessFileAgent.RunAsync(client, new("private selected text", "rewrite"), TestContext.Current.CancellationToken);
        Assert.Equal("suggestion: private selected text", output);
        Assert.Equal("private selected text", client.SeenResult);
        Assert.Equal("rewrite", client.UserPrompt);
        Assert.Equal(["read_selected_content"], client.ToolNames);
    }

    [Fact]
    public async Task OptionalLocalModelReadsSelectedContent()
    {
        if (Environment.GetEnvironmentVariable("RUN_LOCAL_FILE_MODEL_TEST") != "1") return;
        using var client = new OmlxChatClientFactory(new ConfigurationBuilder().Build()).Create(HarnessFileAgent.Model, "http://127.0.0.1:8001/v1");
        int reads = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string result = await HarnessFileAgent.RunAsync(client, new("The blue dog sleeps.", "Read the selected content with the tool, then summarize it in one sentence."), timeout.Token, () => reads++);
        Assert.Equal(1, reads);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void RejectsOversizedAndMissingContentAndInstructions()
    {
        Assert.False(HarnessFileAgent.Valid(new(null, "task")));
        Assert.False(HarnessFileAgent.Valid(new("ok", " ")));
        Assert.False(HarnessFileAgent.Valid(new(new string('é', 65537), "task")));
        Assert.True(HarnessFileAgent.Valid(new(new string('é', 65536), "task")));
    }

    [Fact]
    public async Task EndpointRejectsUntrustedOriginsAndInvalidRequests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Delegation:AllowedOrigin"] = "http://localhost:4000";
        await using var app = builder.Build();
        app.Use(async (context, next) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await next(); });
        app.MapHarnessFileAgent();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-File-Agent", "1");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:4001");
        using var denied = await client.PostAsJsonAsync("/api/file-agent/run", new { content = "x", instruction = "task" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:4000");
        using var invalid = await client.PostAsJsonAsync("/api/file-agent/run", new { content = "x", instruction = "" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True(invalid.Headers.CacheControl?.NoStore);
    }

    private sealed class ScriptClient : IChatClient
    {
        public string? SeenResult { get; private set; }
        public string? UserPrompt { get; private set; }
        public string[] ToolNames { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var all = messages.ToArray();
            var result = all.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            if (result is null)
            {
                UserPrompt = all.Last(m => m.Role == ChatRole.User).Text;
                ToolNames = options!.Tools!.Select(t => t.Name).ToArray();
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("read", "read_selected_content", new Dictionary<string, object?>())])) { FinishReason = ChatFinishReason.ToolCalls });
            }
            SeenResult = result.Result?.ToString();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "suggestion: " + SeenResult)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield break; }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using AGUIWebChat.Server.Agents;
using AGUIWebChat.Server.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class DelegationWorkflowTests
{
    [Fact]
    public async Task EndpointsRequireLocalOriginAndCapabilityAndDoNotCache()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Delegation:AllowedOrigin"] = "http://localhost:4000";
        builder.Services.AddSingleton<IChatClientFactory>(new Factory(new ScriptClient { NoDelegation = true }));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DelegationWorkflow>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DelegationWorkflow>());
        await using var app = builder.Build();
        app.Use(async (context, next) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await next(); });
        app.MapDelegationWorkflow();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var denied = await client.PostAsJsonAsync("/api/delegation/start", new { prompt = "task" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-Delegation-Request", "1");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:4001");
        using var wrongOrigin = await client.PostAsJsonAsync("/api/delegation/start", new { prompt = "task" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:4000");
        using var started = await client.PostAsJsonAsync("/api/delegation/start", new { prompt = "task" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.True(started.Headers.CacheControl?.NoStore);
        var payload = await started.Content.ReadFromJsonAsync<StartResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('A', 64));
        using var wrong = await client.PostAsync("/api/delegation/status", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.Capability);
        using var correct = await client.PostAsync("/api/delegation/status", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
        Assert.True(correct.Headers.CacheControl?.NoStore);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
    private sealed record StartResponse(string Capability);

    [Fact]
    public async Task RealChildSessionIsCapturedAndCapabilityIsIsolated()
    {
        var client = new ScriptClient();
        using var services = Services(client);
        var workflow = new DelegationWorkflow(services.GetRequiredService<IServiceScopeFactory>(), new Lifetime(), TimeProvider.System);
        string token = Assert.IsType<string>(workflow.Start("Original task"));
        Assert.Null(workflow.Get(new string('A', 64)));
        var result = await Finished(workflow, token);
        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.Actors.Count);
        Assert.Equal("Original task", result.Actors[0].Messages[0].Text);
        Assert.Equal("Final answer", result.Actors[0].Messages[1].Text);
        Assert.Equal("Research the question", result.Actors[1].Messages[0].Text);
        Assert.Equal("Actual specialist answer", result.Actors[1].Messages[1].Text);
        Assert.Equal("completed", result.Actors[1].Status);
        Assert.Contains("Actual specialist answer", client.SeenToolResult);
        Assert.True(client.Disposed);
        await workflow.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationAndShutdownAwaitChildBeforeDisposingClient()
    {
        var client = new ScriptClient { BlockChild = true };
        using var services = Services(client);
        var workflow = new DelegationWorkflow(services.GetRequiredService<IServiceScopeFactory>(), new Lifetime(), TimeProvider.System);
        string token = Assert.IsType<string>(workflow.Start("Original task"));
        await client.ChildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("running", Assert.IsType<DelegationSnapshot>(workflow.Get(token)).Actors[1].Status);
        Assert.True(workflow.Cancel(token));
        await client.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var shutdown = workflow.StopAsync(CancellationToken.None);
        Assert.False(shutdown.IsCompleted);
        Assert.False(client.Disposed);
        client.AllowExit.TrySetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task CancelledChildAndCoordinatorAreReportedAfterTeardown()
    {
        var client = new ScriptClient { BlockChild = true };
        using var services = Services(client);
        var workflow = new DelegationWorkflow(services.GetRequiredService<IServiceScopeFactory>(), new Lifetime(), TimeProvider.System);
        string token = Assert.IsType<string>(workflow.Start("task"));
        await client.ChildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(workflow.Cancel(token));
        await client.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        client.AllowExit.TrySetResult();
        var result = await Finished(workflow, token);
        Assert.Equal("cancelled", result.Status);
        Assert.Equal("cancelled", result.Actors[1].Status);
        Assert.True(client.Disposed);
        await workflow.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AdmissionIsBoundedAndNoDelegationIsInvented()
    {
        var client = new ScriptClient { NoDelegation = true };
        using var services = Services(client);
        var workflow = new DelegationWorkflow(services.GetRequiredService<IServiceScopeFactory>(), new Lifetime(), TimeProvider.System);
        Assert.Null(workflow.Start(""));
        Assert.Null(workflow.Start(new string('x', 4001)));
        var tokens = Enumerable.Range(0, 8).Select(_ => Assert.IsType<string>(workflow.Start("task"))).ToArray();
        Assert.Null(workflow.Start("overflow"));
        var result = await Finished(workflow, tokens[0]);
        Assert.Single(result.Actors);
        Assert.Equal("Final answer", result.Actors[0].Messages[1].Text);
        await workflow.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SpecialistAdmissionIsBounded()
    {
        var client = new ScriptClient { RepeatDelegation = true };
        using var services = Services(client);
        var workflow = new DelegationWorkflow(services.GetRequiredService<IServiceScopeFactory>(), new Lifetime(), TimeProvider.System);
        var token = Assert.IsType<string>(workflow.Start("task"));
        var result = await Finished(workflow, token);
        Assert.Equal(4, result.Actors.Count);
        Assert.Equal(3, client.ChildCalls);
        await workflow.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExpiryDeniesAccessAndReclaimsCompletedCapacity()
    {
        var client = new ScriptClient { NoDelegation = true };
        using var services = Services(client);
        var clock = new ManualClock();
        var workflow = new DelegationWorkflow(services.GetRequiredService<IServiceScopeFactory>(), new Lifetime(), clock);
        var tokens = Enumerable.Range(0, 8).Select(_ => Assert.IsType<string>(workflow.Start("task"))).ToArray();
        foreach (var token in tokens) await Finished(workflow, token);
        clock.Now = clock.Now.AddMinutes(15);
        Assert.Null(workflow.Get(tokens[0]));
        Assert.False(workflow.Cancel(tokens[0]));
        Assert.NotNull(workflow.Start("new task"));
        await workflow.StopAsync(CancellationToken.None);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<DelegationSnapshot> Finished(DelegationWorkflow workflow, string token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = Assert.IsType<DelegationSnapshot>(workflow.Get(token));
            if (state.Status != "running") return state;
            await Task.Delay(10, timeout.Token);
        }
    }
    private static ServiceProvider Services(ScriptClient client) => new ServiceCollection().AddSingleton<IChatClientFactory>(new Factory(client)).BuildServiceProvider();
    private sealed class Factory(ScriptClient client) : IChatClientFactory
    {
        public Task<DefaultChatClient> CreateDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DefaultChatClient(client, null, default!));
        public Task<IChatClient> CreateAsync(int modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProviderInferenceCapabilities> GetCapabilitiesAsync(int modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int?> GetDefaultModelIdAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class ScriptClient : IChatClient
    {
        public bool BlockChild { get; init; }
        public bool NoDelegation { get; init; }
        public bool RepeatDelegation { get; init; }
        public int ChildCalls { get; private set; }
        public bool Disposed { get; private set; }
        public string SeenToolResult { get; private set; } = "";
        public TaskCompletionSource ChildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            bool coordinator = options?.Tools?.Any(t => t.Name == "delegate_task") == true || messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
            if (!coordinator)
            {
                ChildCalls++;
                ChildStarted.TrySetResult();
                if (BlockChild)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { CancellationObserved.TrySetResult(); await AllowExit.Task; throw; }
                }
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Actual specialist answer"));
            }
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            var count = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();
            if (!NoDelegation && (result is null || (RepeatDelegation && count < 4)))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{count}", "delegate_task", new Dictionary<string, object?> { ["task"] = "Research the question" })])) { FinishReason = ChatFinishReason.ToolCalls };
            SeenToolResult = result?.Result?.ToString() ?? "";
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final answer"));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield return new ChatResponseUpdate(ChatRole.Assistant, "Unexpected streaming call"); }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }
}

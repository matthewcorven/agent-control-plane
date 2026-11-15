using AGUIWebChat.Server.Endpoints;
using AGUIWebChat.Server.Inference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Microsoft.Extensions.AI;

namespace AGUIWebChat.Server.Tests;

public sealed class E2EChatScriptsTests
{
    [Fact]
    public async Task CheckpointsAndRunStateAreIndependentPerScript()
    {
        using var scripts = new E2EChatScripts();
        var firstId = scripts.Add([new("first", "First"), new("first", " tail", WaitForRelease: true)]);
        var secondId = scripts.Add([new("second", "Second"), new("second", " tail", WaitForRelease: true)]);
        await using var first = scripts.RunAsync(firstId, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var second = scripts.RunAsync(secondId, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.NotEqual(firstId, secondId);
        Assert.True(await first.MoveNextAsync());
        Assert.Equal("First", first.Current.Text);
        Assert.Equal("first", first.Current.MessageId);
        Assert.True(await second.MoveNextAsync());
        Assert.Equal("Second", second.Current.Text);
        var firstBlocked = first.MoveNextAsync().AsTask();
        var secondBlocked = second.MoveNextAsync().AsTask();
        Assert.False(firstBlocked.IsCompleted);
        Assert.False(secondBlocked.IsCompleted);

        Assert.True(scripts.Release(secondId));
        Assert.True(await secondBlocked.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(" tail", second.Current.Text);
        Assert.False(firstBlocked.IsCompleted);
        Assert.True(scripts.Release(firstId));
        Assert.True(await firstBlocked.WaitAsync(TestContext.Current.CancellationToken));
        Assert.False(await first.MoveNextAsync());
        Assert.False(await second.MoveNextAsync());
    }

    [Fact]
    public async Task RemovalCancelsBlockedStreamAndRemovesItsControls()
    {
        using var scripts = new E2EChatScripts();
        var id = scripts.Add([new("answer", "Partial"), new("answer", WaitForRelease: true)]);
        await using var stream = scripts.RunAsync(id, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await stream.MoveNextAsync());
        var blocked = stream.MoveNextAsync().AsTask();
        Assert.False(blocked.IsCompleted);

        Assert.True(scripts.Remove(id));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.False(scripts.Release(id));
        Assert.False(scripts.Remove(id));
        Assert.Throws<InvalidOperationException>(() => scripts.RunAsync(id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestCancellationReleasesTheCheckpointWait()
    {
        using var scripts = new E2EChatScripts();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var id = scripts.Add([new("answer", "Partial"), new("answer", WaitForRelease: true)]);
        await using var stream = scripts.RunAsync(id, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await stream.MoveNextAsync());
        var blocked = stream.MoveNextAsync().AsTask();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.True(scripts.Remove(id));
    }

    [Fact]
    public async Task CancellationStatusSurvivesInlineCheckpointUnwindAfterAToolContinuation()
    {
        using var scripts = new E2EChatScripts();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var arguments = JsonDocument.Parse("{}");
        var id = scripts.Add([
            new("create", Tool: "create_plan", Arguments: arguments.RootElement.Clone()),
            new("update", Text: "Must not arrive.", WaitForRelease: true)
        ]);
        string callId = "";
        await foreach (var update in scripts.RunAsync(id, cancellation.Token))
            callId = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents)).CallId;
        var history = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, """{"plan":{}}""")]);
        await using var stream = scripts.RunAsync(id, cancellation.Token, [history]).GetAsyncEnumerator(cancellation.Token);
        var previous = SynchronizationContext.Current;
        Task<bool> blocked;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
            blocked = stream.MoveNextAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.False(blocked.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        var status = JsonSerializer.SerializeToElement(scripts.Status(id));
        Assert.Equal(2, status.GetProperty("calls").GetInt32());
        Assert.Equal("""{"plan":{}}""", Assert.Single(status.GetProperty("results").EnumerateArray()).GetString());
        Assert.True(status.GetProperty("cancelled").GetBoolean());
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    [Fact]
    public async Task CompletedScriptIsNotCancellationWhenItsCallerLaterStops()
    {
        using var scripts = new E2EChatScripts();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var id = scripts.Add([new("answer", "Complete"), new("answer", " tail", WaitForRelease: true)]);
        Assert.True(scripts.Release(id));
        List<string?> output = [];
        await foreach (var update in scripts.RunAsync(id, cancellation.Token))
            output.Add(update.Text);
        Assert.Equal(["Complete", " tail"], output);

        await cancellation.CancelAsync();

        var status = JsonSerializer.SerializeToElement(scripts.Status(id));
        Assert.Equal(1, status.GetProperty("calls").GetInt32());
        Assert.False(status.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task AlreadyCancelledCallerCannotProduceAStepAndIsObservedAsCancellation()
    {
        using var scripts = new E2EChatScripts();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var id = scripts.Add([new("answer", "Must not arrive.")]);
        await cancellation.CancelAsync();
        await using var stream = scripts.RunAsync(id, cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());

        Assert.True(JsonSerializer.SerializeToElement(scripts.Status(id)).GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task ShutdownCancelsAllOutstandingScriptWaits()
    {
        using var scripts = new E2EChatScripts();
        var firstId = scripts.Add([new("first", WaitForRelease: true)]);
        var secondId = scripts.Add([new("second", WaitForRelease: true)]);
        await using var first = scripts.RunAsync(firstId, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var second = scripts.RunAsync(secondId, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var firstBlocked = first.MoveNextAsync().AsTask();
        var secondBlocked = second.MoveNextAsync().AsTask();

        scripts.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstBlocked);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondBlocked);
        Assert.False(scripts.Release(firstId));
        Assert.False(scripts.Release(secondId));
    }

    [Fact]
    public async Task RemovalBeforeEnumerationStillCancelsWithoutDisposedTokenErrors()
    {
        using var scripts = new E2EChatScripts();
        var id = scripts.Add([new("answer", "text")]);
        var pending = scripts.RunAsync(id, TestContext.Current.CancellationToken);
        Assert.True(scripts.Remove(id));
        await using var stream = pending.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
    }

    [Fact]
    public async Task ErrorKeepsTheEarlierDeltaAndCannotReplaySharedCheckpoints()
    {
        using var scripts = new E2EChatScripts();
        var id = scripts.Add([new("answer", "Partial"), new("answer", Error: "Upstream failed")]);
        await using var stream = scripts.RunAsync(id, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("Partial", stream.Current.Text);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.MoveNextAsync());
        Assert.Equal("Upstream failed", error.Message);
        await using var replay = scripts.RunAsync(id, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var replayError = await Assert.ThrowsAsync<InvalidOperationException>(async () => await replay.MoveNextAsync());
        Assert.Equal("Each E2E chat script can run only once.", replayError.Message);
    }

    [Theory]
    [InlineData("Production", true, true)]
    [InlineData("Staging", true, true)]
    [InlineData("Testing", true, true)]
    [InlineData("Development", false, true)]
    [InlineData("Development", true, false)]
    [InlineData("Development", false, false)]
    public async Task ScriptEndpointsAreAbsentOutsideTheCompleteE2EGate(string environment, bool stub, bool memory)
    {
        await using var app = await CreateAppAsync(environment, stub, memory);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/e2e/chat-scripts/",
            new[] { new E2EChatStep("answer", "text") }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EnabledEndpointsArrangeReleaseAndCleanUpOneScript()
    {
        await using var app = await CreateAppAsync("Development", true, true);
        using var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync("/api/e2e/chat-scripts/",
            new[] { new E2EChatStep("answer", "text") }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = created.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(E2EChatScripts.PromptPrefix + id, created.GetProperty("prompt").GetString());

        using var release = await client.PostAsync($"/api/e2e/chat-scripts/{id}/release",
            null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
        using var removed = await client.DeleteAsync($"/api/e2e/chat-scripts/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var missing = await client.PostAsync($"/api/e2e/chat-scripts/{id}/release",
            null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"messageId\":\"answer\"}]")]
    [InlineData("[{\"messageId\":\" \",\"text\":\"text\"}]")]
    public async Task InvalidScriptBodiesReturnBadRequestInsteadOfLeakingState(string json)
    {
        await using var app = await CreateAppAsync("Development", true, true);
        using var client = app.GetTestClient();
        using var body = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/e2e/chat-scripts/", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RealToolContinuationRetainsArgumentsResultsAndReasoning()
    {
        using var scripts = new E2EChatScripts();
        using var arguments = JsonDocument.Parse("""{"modelId":7}""");
        var id = scripts.Add([
            new("tool", Tool: "switch_active_model", Arguments: arguments.RootElement.Clone()),
            new("reason", Reasoning: "Compare the result."),
            new("answer", Text: "Confirmed")
        ]);
        var callId = "";
        await foreach (var update in scripts.RunAsync(id, TestContext.Current.CancellationToken))
        {
            var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
            callId = call.CallId;
            Assert.Equal("switch_active_model", call.Name);
            Assert.Equal(7, Assert.IsType<JsonElement>(call.Arguments!["modelId"]).GetInt32());
        }
        List<ChatResponseUpdate> continuation = [];
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, """{"switched":true}""")]);
        await foreach (var update in scripts.RunAsync(id, TestContext.Current.CancellationToken, [result]))
            continuation.Add(update);
        Assert.Equal("Compare the result.", Assert.IsType<TextReasoningContent>(Assert.Single(continuation[0].Contents)).Text);
        Assert.Equal("Confirmed", continuation[1].Text);
        var status = JsonSerializer.SerializeToElement(scripts.Status(id));
        Assert.Equal(2, status.GetProperty("calls").GetInt32());
        Assert.Equal("""{"switched":true}""", Assert.Single(status.GetProperty("results").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task ToolContinuationCannotEnterAnActiveEnumerator()
    {
        using var scripts = new E2EChatScripts();
        using var arguments = JsonDocument.Parse("{}");
        var id = scripts.Add([new("tool", Tool: "list_available_models", Arguments: arguments.RootElement.Clone())]);
        await using var first = scripts.RunAsync(id, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await first.MoveNextAsync());
        await using var concurrent = scripts.RunAsync(id, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await concurrent.MoveNextAsync());
    }

    [Fact]
    public void ReleaseControlsAreBoundedPerScript()
    {
        using var scripts = new E2EChatScripts();
        var id = scripts.Add([new("blocked", WaitForRelease: true)]);
        Assert.True(scripts.Release(id));
        Assert.False(scripts.Release(id));
        Assert.Equal(0, JsonSerializer.SerializeToElement(scripts.Status(id)).GetProperty("calls").GetInt32());
    }

    private static async Task<WebApplication> CreateAppAsync(string environment, bool stub, bool memory)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["E2E:StubInference"] = stub.ToString(),
            ["E2E:InMemoryDatabase"] = memory.ToString()
        });
        builder.AddE2EChatScripts();
        var app = builder.Build();
        app.MapE2EChatScripts();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}

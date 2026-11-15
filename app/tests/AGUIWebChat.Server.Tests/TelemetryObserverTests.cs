using System.Runtime.CompilerServices;
using AGUIWebChat.Contracts;
using AGUIWebChat.Server.Agents;
using AGUIWebChat.Server.Hubs;
using AGUIWebChat.Server.Inference;
using AGUIWebChat.Server.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using OllamaSharp.Models.Chat;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public sealed class TelemetryObserverTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReasoningIsPerRunWithoutAnActivityListenerAndDeliveryFailureDoesNotBreakStreaming()
    {
        var sent = new List<(string Method, object Payload)>();
        var client = Proxy<IClientProxy>((_, args) =>
        {
            sent.Add(((string)args[0]!, ((object[])args[1]!)[0]));
            return Task.FromException(new IOException("Disconnected telemetry client"));
        });
        var clients = Proxy<IHubClients>((method, args) =>
        {
            Assert.Equal("Group", method.Name);
            Assert.Equal("channel", args[0]);
            return client;
        });
        var hub = Proxy<IHubContext<TelemetryHub>>((_, _) => clients);
        var agent = new ReasoningClient().AsAIAgent();
        for (int run = 1; run <= 2; run++)
        {
            var updates = new List<AgentResponseUpdate>();
            await foreach (var update in ReasoningTelemetry.ObserveAsync(
                [new(ChatRole.User, "Reason")], null, null, agent, hub,
                "channel", $"run-{run}", "test", "model", "high", Cancellation))
                updates.Add(update);
            Assert.Equal("First answer.Second answer.", string.Concat(updates.Select(u => u.Text)));
        }
        var reasoning = sent.Where(item => item.Method == "ReasoningCompleted")
            .Select(item => Assert.IsType<ReasoningTelemetryDto>(item.Payload)).ToArray();
        Assert.Equal(2, reasoning.Length);
        for (int run = 1; run <= 2; run++)
        {
            Assert.Equal($"run-{run}", reasoning[run - 1].RunId);
            Assert.Equal(3, reasoning[run - 1].ChunkCount);
            Assert.Equal("onetwothree".Length, reasoning[run - 1].CharacterCount);
            Assert.True(reasoning[run - 1].DurationMs > 0);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingRealRunIdLogsAndSkipsBothPublications(string runId)
    {
        var sink = new Events();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
        try
        {
            int calls = 0;
            var hub = Proxy<IHubContext<TelemetryHub>>((_, _) => { calls++; throw new IOException(); });
            await TelemetryPublisher.SendAsync(hub, "channel", "ReasoningCompleted",
                new ReasoningTelemetryDto(runId, 1, 1, 3), Cancellation);
            await TelemetryPublisher.SendAsync(hub, "channel", "LlmMetricsCompleted",
                new LlmTelemetryDto(runId, "agent", "model", 1, 2, 3, 10, 200,
                    null, null, null, null, null, null), Cancellation);
            Assert.Equal(0, calls);
            Assert.Equal(2, sink.Items.Count(e => e.Level == LogEventLevel.Warning
                && e.RenderMessage().Contains("RunId", StringComparison.Ordinal)));
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public async Task OllamaRawCountersSupplyTokensAndNanosecondTiming()
    {
        LlmTelemetryDto? metrics = null;
        var client = Proxy<IClientProxy>((_, args) =>
        {
            metrics = Assert.IsType<LlmTelemetryDto>(((object[])args[1]!)[0]);
            return Task.CompletedTask;
        });
        var hub = Proxy<IHubContext<TelemetryHub>>((_, _) =>
            Proxy<IHubClients>((_, _) => client));
        var raw = new ChatDoneResponseStream
        {
            PromptEvalCount = 12, EvalCount = 8, TotalDuration = 2_000_000_000,
            LoadDuration = 100_000_000, PromptEvalDuration = 400_000_000, EvalDuration = 1_000_000_000
        };
        async IAsyncEnumerable<AgentResponseUpdate> Updates()
        {
            await Task.Yield();
            yield return new() { RawRepresentation = new ChatResponseUpdate { RawRepresentation = raw } };
        }
        await foreach (var _ in LlmTelemetry.ObserveAsync(Updates(), hub, "channel", "real-run",
            "agent", "model", Cancellation)) { }
        Assert.NotNull(metrics);
        Assert.Equal((12, 8, 20), (metrics.InputTokens, metrics.OutputTokens, metrics.TotalTokens));
        Assert.Equal(2000, metrics.OllamaTotalDurationMs);
        Assert.Equal(100, metrics.LoadDurationMs);
        Assert.Equal(400, metrics.PromptEvalDurationMs);
        Assert.Equal(1000, metrics.EvalDurationMs);
        Assert.Equal(30, metrics.PromptTokensPerSecond);
        Assert.Equal(8, metrics.GenerationTokensPerSecond);
        Assert.True(metrics.RequestDurationMs > 0);
    }

    private sealed class ReasoningClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var content in new AIContent[] { new TextReasoningContent("one"),
                new TextReasoningContent("two"), new TextContent("First answer."),
                new TextReasoningContent("three"), new TextContent("Second answer.") })
                yield return new(ChatRole.Assistant, [content]) { MessageId = "answer" };
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Events : ILogEventSink
    {
        public List<LogEvent> Items { get; } = [];
        public void Emit(LogEvent logEvent) => Items.Add(logEvent);
    }

    private static T Proxy<T>(Func<System.Reflection.MethodInfo, object?[], object?> callback) where T : class
    {
        var proxy = System.Reflection.DispatchProxy.Create<T, TelemetryTests.Stub>();
        ((TelemetryTests.Stub)(object)proxy).Call = callback;
        return proxy;
    }
}

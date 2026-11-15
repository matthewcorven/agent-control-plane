using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using AGUIWebChat.Client.Models;
using AGUIWebChat.Client.Services;
using AGUIWebChat.Middleware;
using AGUIWebChat.Server.Inference;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class ClientSettingsRoundTripTests
{
    [Theory]
    [InlineData(ThinkingEffort.Low, 0, 0, 1, 1024)]
    [InlineData(ThinkingEffort.High, 2, 1, 1000, 131072)]
    [InlineData(ThinkingEffort.Medium, 0.31, 0.53, 17, 8192)]
    public async Task ActualClientPayloadSurvivesServerRunInputExtractionAndUsesFreshSettingsOnNextSend(
        ThinkingEffort effort, double temperature, double topP, int topK, int numCtx)
    {
        var module = new CaptureModule();
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        await using var telemetry = new TelemetryService("http://127.0.0.1:1/telemetry");
        using var service = new UiAgentChatService(bridge, telemetry, new EventLogger()) { ModelId = 17 };
        var first = new ChatSettings
        {
            ThinkingEffort = effort, Temperature = temperature, TopP = topP, TopK = topK, NumCtx = numCtx
        };

        await SendAsync(service, first);
        var firstInput = Assert.Single(module.Inputs);
        AssertSettings(firstInput, first, 17, telemetry.Channel);
        Assert.Equal(firstInput.RunId, service.ActiveRunId);

        var second = new ChatSettings
        {
            ThinkingEffort = effort == ThinkingEffort.Low ? ThinkingEffort.High : ThinkingEffort.Low,
            Temperature = 2 - temperature, TopP = 1 - topP,
            TopK = topK == 1 ? 1000 : 1, NumCtx = numCtx == 1024 ? 131072 : 1024
        };
        service.ModelId = 23;
        await SendAsync(service, second);

        Assert.Equal(2, module.Inputs.Count);
        var secondInput = module.Inputs[1];
        AssertSettings(secondInput, second, 23, telemetry.Channel);
        Assert.Equal(firstInput.ThreadId, secondInput.ThreadId);
        Assert.NotEqual(firstInput.RunId, secondInput.RunId);
        Assert.Equal(secondInput.RunId, service.ActiveRunId);
        AssertSettings(firstInput, first, 17, telemetry.Channel);
    }

    private static async Task SendAsync(UiAgentChatService service, ChatSettings settings)
    {
        await foreach (var _ in service.SendAsync("Settings round-trip", settings, TestContext.Current.CancellationToken)) { }
    }

    private static void AssertSettings(RunAgentInput input, ChatSettings expected, int modelId, string channel)
    {
        var context = input.ToChatRequestContext(new(JsonSerializerDefaults.Web));
        Assert.True(context.ChatOptions.TryGetRunAgentInput(out var extracted));
        Assert.Equal(input.ThreadId, extracted.ThreadId);
        Assert.Equal(input.RunId, extracted.RunId);
        Assert.False(string.IsNullOrWhiteSpace(extracted.ThreadId));
        Assert.False(string.IsNullOrWhiteSpace(extracted.RunId));
        var state = extracted.State!.Value;
        Assert.Equal(modelId, state.GetProperty("modelId").GetInt32());
        Assert.Equal(channel, state.GetProperty("telemetryChannel").GetString());
        Assert.Equal(expected.ThinkingEffort.ToString(), state.GetProperty("thinkingEffort").GetString());
        var settings = InferenceSettingsParser.GetInferenceSettings(
            new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions },
            new() { ThinkingEffort = "fallback", Temperature = -1, TopP = -1, TopK = -1, NumCtx = -1 });
        Assert.Equal(expected.ThinkingEffort.ToString().ToLowerInvariant(), settings.ThinkingEffort);
        Assert.Equal(expected.Temperature, settings.Temperature);
        Assert.Equal(expected.TopP, settings.TopP);
        Assert.Equal(expected.TopK, settings.TopK);
        Assert.Equal(expected.NumCtx, settings.NumCtx);
    }

    private sealed class EventLogger : IAgUiSseEventLogger
    {
        public void LogEvent(string sseEvent) { }
    }

    private sealed class Runtime(CaptureModule module) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            ValueTask.FromResult((T)(object)module);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((T)(object)module);
    }

    private sealed class CaptureModule : IJSObjectReference
    {
        private readonly MemoryStream _request = new();
        private byte[] _response = [];
        private int _at;
        public List<RunAgentInput> Inputs { get; } = [];
#pragma warning disable BL0016 // Only the JS boundary is fake; UIAgent and both AG-UI serializers are real.
        public ValueTask<T> InvokeAsync<T>(string operation, object?[]? args) => Execute<T>(operation, args);
        public ValueTask<T> InvokeAsync<T>(string operation, CancellationToken cancellationToken, object?[]? args) =>
            Execute<T>(operation, args);
        private ValueTask<T> Execute<T>(string operation, object?[]? args)
        {
            switch (operation)
            {
                case "create":
                    Assert.Equal("/ag-ui", args![0]);
                    _request.SetLength(0);
                    return ValueTask.FromResult((T)(object)1);
                case "append": _request.Write((byte[])args![1]!); break;
                case "start":
                    var input = JsonSerializer.Deserialize(_request.ToArray(), AGUIJsonSerializerContext.Default.RunAgentInput)!;
                    Inputs.Add(input);
                    BaseEvent[] frames =
                    [
                        new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId },
                        new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId }
                    ];
                    _response = Encoding.UTF8.GetBytes(string.Concat(frames.Select(frame =>
                        "data: " + JsonSerializer.Serialize(frame, AGUIJsonSerializerContext.Default.BaseEvent) + "\n\n")));
                    _at = 0;
                    return ValueTask.FromResult(JsonSerializer.Deserialize<T>(
                        """{"status":200,"contentType":"text/event-stream"}""", JsonSerializerOptions.Web)!);
                case "read":
                    int count = Math.Min(8192, _response.Length - _at);
                    var chunk = _response.AsSpan(_at, count).ToArray();
                    _at += count;
                    return ValueTask.FromResult((T)(object)chunk);
                case "abort": break;
                default: throw new InvalidOperationException($"Unexpected browser operation: {operation}");
            }
            return ValueTask.FromResult(default(T)!);
        }
#pragma warning restore BL0016
        public ValueTask DisposeAsync()
        {
            _request.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

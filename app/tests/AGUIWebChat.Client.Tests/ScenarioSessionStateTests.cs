using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUIWebChat.Client.Models;
using AGUIWebChat.Client.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class ScenarioSessionStateTests
{
    [Fact]
    public async Task LiveSnapshotReplacesRootAndPlanDeltaPersistsOnlyTheSelectedStep()
    {
        var module = new StateModule(input =>
        {
            var snapshot = Snapshot(input);
            return
            [
                new StateSnapshotEvent { Snapshot = JsonSerializer.SerializeToElement(snapshot) },
                new StateDeltaEvent { Delta = JsonSerializer.SerializeToElement(new[]
                {
                    new { op = "replace", path = "/plan/steps/1/status", value = "done" }
                }) }
            ];
        });
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        await using var telemetry = new TelemetryService("http://127.0.0.1:1/telemetry");
        await using var session = Session(bridge, telemetry);
        var settings = new ChatSettings { Temperature = 0.31, TopK = 17 };
        await session.InitializeAsync(7, settings);
        session.Checkpoint.State["obsolete"] = true;

        await session.SendAsync("Create and update the plan", settings);

        Assert.Null(session.Error);
        Assert.False(session.RestartRequired);
        Assert.False(session.IsSending);
        Assert.False(session.Checkpoint.State.ContainsKey("obsolete"));
        Assert.Equal("Launch", session.Plan!.Title);
        Assert.Equal(["pending", "done"], session.Plan.Steps.Select(step => step.Status));
        Assert.Equal(["step-1", "step-2"], session.Plan.Steps.Select(step => step.Id));
        Assert.Equal(["Build", "Ship"], session.Plan.Steps.Select(step => step.Title));
        Assert.Equal(" Exact title ", session.Document.Title);
        Assert.Equal(" \t", session.Document.Text);
        Assert.Equal(0.31, session.Checkpoint.State["temperature"]!.GetValue<double>());
        Assert.Equal(17, session.Checkpoint.State["topK"]!.GetValue<int>());
        Assert.Equal(7, session.Checkpoint.State["modelId"]!.GetValue<int>());
        Assert.Equal(telemetry.Channel, session.Checkpoint.State["telemetryChannel"]!.GetValue<string>());
        Assert.True(session.Checkpoint.State["future"]!["keep"]!.GetValue<bool>());
        var snapshots = module.Saved.Where(saved => saved.State.ContainsKey("plan")).ToArray();
        Assert.Equal("pending", snapshots[0].State["plan"]!["steps"]![1]!["status"]!.GetValue<string>());
        Assert.Equal("done", snapshots[1].State["plan"]!["steps"]![1]!["status"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(snapshots[0].State["document"], snapshots[1].State["document"]));
        Assert.Equal(session.Checkpoint.ThreadId, snapshots[^1].ThreadId);
        Assert.True(JsonNode.DeepEquals(session.Checkpoint.State, snapshots[^1].State));
        Assert.Equal(0, module.Clears);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidTypedPlanFromLiveSnapshotOrDeltaCannotPublishOrPersist(bool snapshot)
    {
        var module = new StateModule(input =>
        {
            var valid = Snapshot(input);
            var invalid = (JsonObject)valid.DeepClone();
            invalid["plan"]!["steps"]![0]!["status"] = "not-a-plan-status";
            return
            [
                new StateSnapshotEvent { Snapshot = JsonSerializer.SerializeToElement(valid) },
                snapshot
                    ? new StateSnapshotEvent { Snapshot = JsonSerializer.SerializeToElement(invalid) }
                    : new StateDeltaEvent { Delta = JsonSerializer.SerializeToElement(new[]
                    {
                        new { op = "replace", path = "/plan/steps/0/status", value = "not-a-plan-status" }
                    }) }
            ];
        });
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        await using var telemetry = new TelemetryService("http://127.0.0.1:1/telemetry");
        await using var session = Session(bridge, telemetry);
        var settings = new ChatSettings();
        await session.InitializeAsync(7, settings);

        await session.SendAsync("Reject invalid state", settings);

        Assert.NotNull(session.Error);
        Assert.True(session.RestartRequired);
        Assert.False(session.IsSending);
        Assert.Equal(["pending", "pending"], session.Plan!.Steps.Select(step => step.Status));
        Assert.Equal(" Exact title ", session.Document.Title);
        Assert.Equal(1, module.Clears);
        var savedPlan = Assert.Single(module.Saved, saved => saved.State.ContainsKey("plan"));
        Assert.True(JsonNode.DeepEquals(session.Checkpoint.State, savedPlan.State));
        Assert.DoesNotContain("not-a-plan-status", JsonSerializer.Serialize(savedPlan, ScenarioJsonContext.Default.ScenarioCheckpoint));
    }

    private static ScenarioSession Session(BrowserAgUiBridge bridge, TelemetryService telemetry) =>
        new(bridge, telemetry, NullLogger.Instance, "generative-ui", action =>
        {
            action();
            return Task.CompletedTask;
        });

    private static JsonObject Snapshot(RunAgentInput input)
    {
        var state = JsonNode.Parse(input.State!.Value.GetRawText())!.AsObject();
        state.Remove("obsolete");
        state["document"] = JsonNode.Parse("""{"title":" Exact title ","text":" \t"}""");
        state["plan"] = JsonNode.Parse("""{"title":"Launch","steps":[{"id":"step-1","title":"Build","status":"pending"},{"id":"step-2","title":"Ship","status":"pending"}]}""");
        state["future"] = new JsonObject { ["keep"] = true };
        return state;
    }

    private sealed class Runtime(StateModule module) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            ValueTask.FromResult((T)(object)module);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((T)(object)module);
    }

    private sealed class StateModule(Func<RunAgentInput, BaseEvent[]> events) : IJSObjectReference
    {
        private readonly MemoryStream _request = new();
        private readonly MemoryStream _checkpoint = new();
        private byte[] _response = [];
        private int _at;
        public List<ScenarioCheckpoint> Saved { get; } = [];
        public int Clears { get; private set; }
#pragma warning disable BL0016 // In-process browser boundary; production transport and state observers remain real.
        public ValueTask<T> InvokeAsync<T>(string operation, object?[]? args) => Execute<T>(operation, args);
        public ValueTask<T> InvokeAsync<T>(string operation, CancellationToken cancellationToken, object?[]? args) =>
            Execute<T>(operation, args);
        private ValueTask<T> Execute<T>(string operation, object?[]? args)
        {
            switch (operation)
            {
                case "beginCheckpoint":
                    _checkpoint.SetLength(0);
                    return Json<T>(args![1] is true ? """{"id":0,"length":0}""" : """{"id":2,"length":0}""");
                case "appendCheckpoint": _checkpoint.Write((byte[])args![1]!); break;
                case "commitCheckpoint":
                    Saved.Add(JsonSerializer.Deserialize(_checkpoint.ToArray(), ScenarioJsonContext.Default.ScenarioCheckpoint)!);
                    break;
                case "endCheckpoint": break;
                case "clear": Clears++; break;
                case "create": _request.SetLength(0); return ValueTask.FromResult((T)(object)1);
                case "append": _request.Write((byte[])args![1]!); break;
                case "start":
                    var input = JsonSerializer.Deserialize(_request.ToArray(), AGUIJsonSerializerContext.Default.RunAgentInput)!;
                    BaseEvent[] frames =
                    [
                        new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId },
                        .. events(input),
                        new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId }
                    ];
                    _response = Encoding.UTF8.GetBytes(string.Concat(frames.Select(frame =>
                        "data: " + JsonSerializer.Serialize(frame, AGUIJsonSerializerContext.Default.BaseEvent) + "\n\n")));
                    _at = 0;
                    return Json<T>("""{"status":200,"contentType":"text/event-stream"}""");
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
        private static ValueTask<T> Json<T>(string json) =>
            ValueTask.FromResult(JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!);
#pragma warning restore BL0016
        public ValueTask DisposeAsync()
        {
            _request.Dispose();
            _checkpoint.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

using AGUIWebChat.Client.Models;
using AGUIWebChat.Client.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class ScenarioCheckpointTests
{
    [Theory]
    [InlineData("switch_active_model", "{}")]
    [InlineData("switch_active_model", """{"modelId":0}""")]
    [InlineData("propose_document_edit", """{"title":"Valid","text":""}""")]
    [InlineData("propose_document_edit", """{"title":"Valid","text":"","originalTitle":"","originalText":null}""")]
    public async Task SemanticCorruptionClearsCheckpointWithoutPublishingAnyRestoredState(string name, string arguments)
    {
        var restored = Checkpoint(name, arguments);
        var module = new CheckpointModule(JsonSerializer.Serialize(restored, ScenarioJsonContext.Default.ScenarioCheckpoint));
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        await using var telemetry = new TelemetryService("http://127.0.0.1:1/telemetry");
        await using var session = new ScenarioSession(bridge, telemetry, NullLogger.Instance, "approval", action =>
        {
            action();
            return Task.CompletedTask;
        });
        var original = session.Checkpoint;
        await Assert.ThrowsAsync<JsonException>(() => session.InitializeAsync(7, new()));
        Assert.Same(original, session.Checkpoint);
        Assert.Empty(session.Checkpoint.State);
        Assert.Empty(session.Checkpoint.Tools);
        Assert.Empty(session.Checkpoint.History);
        Assert.Empty(session.Checkpoint.Transcript);
        Assert.Null(session.Checkpoint.Reasoning);
        Assert.Equal(0, module.Saves);
        Assert.Equal(1, module.Clears);
    }

    [Theory]
    [InlineData("switch_active_model", """{ "modelId":7, "opaque":{"retained":true} }""")]
    [InlineData("propose_document_edit", """{"title":" Proposed ","text":"","originalTitle":"","originalText":" \t"}""")]
    public async Task ValidPendingCheckpointRetainsRootInferenceHistoryAndExactArguments(string name, string arguments)
    {
        var restored = Checkpoint(name, arguments);
        var module = new CheckpointModule(JsonSerializer.Serialize(restored, ScenarioJsonContext.Default.ScenarioCheckpoint));
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        await using var telemetry = new TelemetryService("http://127.0.0.1:1/telemetry");
        await using var session = new ScenarioSession(bridge, telemetry, NullLogger.Instance, "approval", action =>
        {
            action();
            return Task.CompletedTask;
        });
        await session.InitializeAsync(99, new());
        Assert.Equal(restored.ThreadId, session.Checkpoint.ThreadId);
        Assert.True(JsonNode.DeepEquals(restored.State, session.Checkpoint.State));
        Assert.True(JsonNode.DeepEquals(restored.History, session.Checkpoint.History));
        Assert.Equal(2, session.Checkpoint.Tools.Count);
        Assert.Equal(arguments, session.Checkpoint.Tools[1].Arguments);
        Assert.Null(session.Checkpoint.Tools[0].Approved);
        Assert.Equal("Saved transcript", Assert.Single(session.Checkpoint.Transcript).Text);
        Assert.Equal("Saved reasoning", session.Checkpoint.Reasoning!.Text);
        Assert.Equal(1, module.Saves);
        Assert.Equal(0, module.Clears);
    }

    [Fact]
    public async Task ResetNeverPublishesADocumentlessRootToAnAlreadyReadyRenderer()
    {
        var restored = Checkpoint("switch_active_model", """{"modelId":7}""");
        var module = new CheckpointModule(JsonSerializer.Serialize(restored, ScenarioJsonContext.Default.ScenarioCheckpoint));
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        await using var telemetry = new TelemetryService("http://127.0.0.1:1/telemetry");
        ScenarioSession? current = null;
        bool ready = false;
        await using var session = new ScenarioSession(bridge, telemetry, NullLogger.Instance, "approval", action =>
        {
            action();
            if (ready)
            {
                var document = Assert.IsType<JsonObject>(current!.Checkpoint.State["document"]);
                Assert.IsType<JsonValue>(document["title"], exactMatch: false);
                Assert.IsType<JsonValue>(document["text"], exactMatch: false);
            }
            return Task.CompletedTask;
        });
        current = session;
        await session.InitializeAsync(7, new());
        ready = true;
        await session.ResetAsync(7, new());
        Assert.NotEqual(restored.ThreadId, session.Checkpoint.ThreadId);
        Assert.Equal("Untitled", session.Document.Title);
        Assert.Equal("", session.Document.Text);
        Assert.Empty(session.Checkpoint.Tools);
    }

    private static ScenarioCheckpoint Checkpoint(string name, string arguments) => new()
    {
        Route = "approval",
        State = JsonNode.Parse("""{"modelId":7,"document":{"title":"","text":" \t"},"temperature":0.31,"futureSetting":{"retained":[true,7]}}""")!.AsObject(),
        History = JsonNode.Parse("""[{"id":"history","role":"user","content":"Saved turn"}]""")!.AsArray(),
        Transcript = [new() { Id = "transcript", Text = "Saved transcript", IsUser = true }],
        Reasoning = new() { Text = "Saved reasoning", ChunkCount = 1 },
        Tools = [
            new() { Id = "valid-first", Name = "switch_active_model", Arguments = """{"modelId":7}""", InterruptId = "first", Status = "Approval required" },
            new() { Id = "tool", Name = name, Arguments = arguments, InterruptId = "pending", Status = "Approval required" }
        ]
    };

    private sealed class Runtime(CheckpointModule module) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult((T)(object)module);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((T)(object)module);
    }

    private sealed class CheckpointModule(string saved) : IJSObjectReference
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(saved);
        private int _at;
        private bool _cleared;
        public int Saves { get; private set; }
        public int Clears { get; private set; }
#pragma warning disable BL0016 // In-process test double, not browser interop.
        public ValueTask<T> InvokeAsync<T>(string operation, object?[]? args) => Execute<T>(operation, args);
        public ValueTask<T> InvokeAsync<T>(string operation, CancellationToken cancellationToken, object?[]? args) => Execute<T>(operation, args);
#pragma warning restore BL0016
        private ValueTask<T> Execute<T>(string operation, object?[]? args)
        {
            switch (operation)
            {
                case "beginCheckpoint":
                    return ValueTask.FromResult(JsonSerializer.Deserialize<T>(
                        _cleared && args![1] is true ? """{"id":0,"length":0}""" :
                        $$"""{"id":1,"length":{{_bytes.Length}}}""", JsonSerializerOptions.Web)!);
                case "readCheckpoint":
                    int size = Math.Min(8192, _bytes.Length - _at);
                    var chunk = _bytes.AsSpan(_at, size).ToArray();
                    _at += size;
                    return ValueTask.FromResult((T)(object)chunk);
                case "commitCheckpoint": Saves++; break;
                case "clear": Clears++; _cleared = true; break;
                case "appendCheckpoint":
                case "endCheckpoint": break;
                default: throw new InvalidOperationException($"Unexpected checkpoint operation: {operation}");
            }
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

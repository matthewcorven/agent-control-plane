using AGUI.Abstractions;
using AGUIWebChat.Client.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class BrowserAgUiBridgeTests
{
    [Theory]
    [InlineData("", "agent_stream_incomplete")]
    [InlineData("""data: {"type":"RUN_STARTED","threadId":"unit-thread","runId":"unit-run"}""", "agent_stream_incomplete")]
    [InlineData("""data: {"type":"RUN_STARTED","threadId":"foreign-thread","runId":"unit-run"}""", "agent_stream_identity")]
    [InlineData("""data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"orphan","delta":"Not admitted"}""", "agent_stream_sequence")]
    [InlineData("""data: {"type":"RUN_FINISHED","threadId":"unit-thread","runId":"unit-run"}""", "agent_stream_sequence")]
    public async Task MissingOrForeignExecutionBoundariesCannotBecomeSuccessfulEof(string frame, string code)
    {
        var module = new Module(200, "text/event-stream", frame + "\n\n");
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        var transport = new ScenarioAgUiTransport(bridge, "/ag-ui/tools");
        var error = await Assert.ThrowsAsync<ScenarioTransportException>(async () =>
        {
            await foreach (var _ in transport.SendAsync(Input(), TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal(code, error.Code);
        Assert.Equal(1, module.Aborts);
    }

    [Fact]
    public async Task ExactUtf8SplitsAndBoundedRequestChunksUseTheRealTransportParser()
    {
        var module = new Module(200, "text/event-stream",
            """data: {"type":"RUN_STARTED","threadId":"unit-thread","runId":"unit-run"}""" + "\n\n" +
            """data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"unicode","delta":"🧪 café"}""" + "\n\n" +
            """data: {"type":"RUN_FINISHED","threadId":"unit-thread","runId":"unit-run"}""" + "\n\n", oneByte: true);
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        var transport = new ScenarioAgUiTransport(bridge, "/ag-ui/tools");
        var input = Input();
        input.State = JsonSerializer.SerializeToElement(new { note = new string('x', 17000) });
        List<BaseEvent> observed = [];
        transport.Observe = item => { observed.Add(item); return Task.CompletedTask; };
        await foreach (var item in transport.SendAsync(input, TestContext.Current.CancellationToken))
            Assert.Same(observed.Last(), item);
        Assert.Equal(3, observed.Count);
        Assert.Equal("🧪 café", Assert.Single(observed.OfType<TextMessageContentEvent>()).Delta);
        Assert.True(module.Chunks.Count >= 3);
        Assert.All(module.Chunks, size => Assert.InRange(size, 1, 8192));
        Assert.True(module.Chunks.Sum() > 17000);
        Assert.Equal(1, module.Aborts);
    }

    [Theory]
    [InlineData(409, "application/problem+json", """{"detail":"Consumed","code":"approval_consumed","restartRequired":true}""", "approval_consumed")]
    [InlineData(502, "text/html", "<html>Gateway</html>", "agent_error_format")]
    [InlineData(200, "application/json", "{}", "agent_content_type")]
    public async Task StatusFailuresAreExplicitAndReleaseTheBrowserRequest(int status, string contentType, string body, string code)
    {
        var module = new Module(status, contentType, body);
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        var transport = new ScenarioAgUiTransport(bridge, "/ag-ui/approval");
        var error = await Assert.ThrowsAsync<ScenarioTransportException>(async () =>
        {
            await foreach (var _ in transport.SendAsync(Input(), TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.Status);
        Assert.Equal(1, module.Aborts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseFrameAndErrorBodyLimitsFailVisiblyAndAbort(bool streaming)
    {
        var module = new Module(streaming ? 200 : 500, streaming ? "text/event-stream" : "application/problem+json",
            new string('x', streaming ? 256 * 1024 + 1 : 16 * 1024 + 1));
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        var transport = new ScenarioAgUiTransport(bridge, "/ag-ui/tools");
        var error = await Assert.ThrowsAsync<ScenarioTransportException>(async () =>
        {
            await foreach (var _ in transport.SendAsync(Input(), TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal(streaming ? "browser_event_limit" : "agent_error_limit", error.Code);
        Assert.Equal(1, module.Aborts);
    }

    [Fact]
    public async Task ExplicitOwnerCancellationCannotBecomeSuccessfulEof()
    {
        var module = new Module(200, "text/event-stream", "", blocked: true);
        await using var bridge = new BrowserAgUiBridge(new Runtime(module), NullLogger<BrowserAgUiBridge>.Instance);
        using var owner = new CancellationTokenSource();
        var transport = new ScenarioAgUiTransport(bridge, "/ag-ui/tools") { RunCancellation = () => owner.Token };
        var run = Task.Run(async () =>
        {
            await foreach (var _ in transport.SendAsync(Input())) { }
        }, TestContext.Current.CancellationToken);
        await module.Reading.Task.WaitAsync(TestContext.Current.CancellationToken);
        await owner.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, module.Aborts);
    }

    private static RunAgentInput Input() => new()
    {
        ThreadId = "unit-thread", RunId = "unit-run", Messages = [], Tools = [], Context = [],
        State = JsonSerializer.SerializeToElement(new { document = new { title = "Untitled", text = "" } })
    };

    private sealed class Runtime(Module module) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
            ValueTask.FromResult((T)(object)module);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((T)(object)module);
    }

    private sealed class Module(int status, string contentType, string body, bool oneByte = false, bool blocked = false) : IJSObjectReference
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(body);
        private int _at;
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<int> Chunks { get; } = [];
        public int Aborts { get; private set; }
#pragma warning disable BL0016 // These calls dispatch an in-process test double, not browser interop.
        public ValueTask<T> InvokeAsync<T>(string operation, object?[]? args) => ExecuteAsync<T>(operation, args);
        public ValueTask<T> InvokeAsync<T>(string operation, CancellationToken cancellationToken, object?[]? args) =>
            ExecuteAsync<T>(operation, args);
#pragma warning restore BL0016
        private async ValueTask<T> ExecuteAsync<T>(string operation, object?[]? args)
        {
            switch (operation)
            {
                case "create": return (T)(object)1;
                case "append": Chunks.Add(((byte[])args![1]!).Length); break;
                case "start": return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(new { status, contentType }), JsonSerializerOptions.Web)!;
                case "read":
                    Reading.TrySetResult();
                    if (blocked) await _aborted.Task;
                    int count = Math.Min(oneByte ? 1 : 8192, _bytes.Length - _at);
                    byte[] chunk = _bytes.AsSpan(_at, count).ToArray();
                    _at += count;
                    return (T)(object)chunk;
                case "abort": Aborts++; _aborted.TrySetResult(); break;
                default: throw new InvalidOperationException("Unexpected bridge operation in the test.");
            }
            return default!;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

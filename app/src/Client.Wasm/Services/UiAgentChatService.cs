using AGUI.Abstractions;
using AGUI.Client;
using AGUIWebChat.Client.Formatting;
using AGUIWebChat.Client.Models;
using AGUIWebChat.Middleware;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AGUIWebChat.Client.Services;

/// <summary>
/// Chat service backed by <see cref="UIAgent"/> over the raw <see cref="AGUIChatClient"/>.
/// Replaces the hand-rolled <c>AgUiChatService</c> agent/session loop.
/// </summary>
public sealed class UiAgentChatService : IDisposable
{
    private readonly BrowserAgUiBridge _bridge;
    private readonly IAgUiSseEventLogger _eventLogger;
    private readonly TelemetryService _telemetry;
    private CancellationToken _runToken;
    private int _generation;
    private string? _activeRunId;
    private UIAgent _agent;

    public UiAgentChatService(BrowserAgUiBridge bridge, TelemetryService telemetry, IAgUiSseEventLogger eventLogger)
    {
        _bridge = bridge;
        _eventLogger = eventLogger;
        _telemetry = telemetry;
        _agent = CreateAgent();
    }

    private UIAgent CreateAgent()
    {
        int generation = _generation;
        var transport = new ScenarioAgUiTransport(_bridge, "/ag-ui")
        {
            RunCancellation = () => _runToken,
            Prepare = input => { _activeRunId = input.RunId; return Task.CompletedTask; },
            Observe = item =>
            {
                _eventLogger.LogEvent("data: " + JsonSerializer.Serialize(item, AGUIJsonSerializerContext.Default.BaseEvent));
                return Task.CompletedTask;
            }
        };
        var chatClient = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });

        // UIAgent does not surface ChatResponseUpdates, so wrap the transport to observe
        // the AG-UI run id (RunStartedEvent) and reasoning (TextReasoningContent), which
        // the shipped UIAgent block pipeline does not surface. FormattedChatClient sits
        // outside the observer so it can attach parsed RichTextContent to each update.
        return new UIAgent(new FormattedChatClient(new ChatRunObserverClient(
            chatClient,
            runId => { if (generation == _generation) RunStarted?.Invoke(runId); },
            snapshot =>
            {
                if (generation != _generation || snapshot.RunId != _activeRunId) return;
                LastRun = snapshot;
                RunChanged?.Invoke(snapshot);
            })),
            options =>
            {
                // RawRepresentationFactory is invoked per run, so BuildState() stays fresh.
                options.ChatOptions = new ChatOptions
                {
                    RawRepresentationFactory = _ => new RunAgentInput { State = BuildState() }
                };
            });
    }

    /// <summary>
    /// Current settings snapshot. The page pushes the latest settings before each send;
    /// <see cref="BuildState"/> reads this at run time.
    /// </summary>
    public ChatSettings Settings { get; set; } = new();

    public int? ModelId { get; set; }

    public string? ActiveRunId => _activeRunId;

    public UIAgent Agent => _agent;

    public event Action<string>? RunStarted;

    public event Action<ChatRunSnapshot>? RunChanged;

    /// <summary>
    /// Latest observed run snapshot; updated whenever <see cref="RunChanged"/> fires.
    /// </summary>
    public ChatRunSnapshot? LastRun { get; private set; }

    /// <summary>
    /// Disposes the current agent and rebuilds it, clearing <see cref="LastRun"/>.
    /// </summary>
    public void Reset()
    {
        _generation++;
        _activeRunId = null;
        _agent.Dispose();
        _agent = CreateAgent();
        LastRun = null;
        ModelId = null;
    }

    public async IAsyncEnumerable<ContentBlock> SendAsync(
        string message,
        ChatSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Settings = settings;
        _runToken = cancellationToken;

        await foreach (var block in _agent.SendMessageAsync(new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, message), cancellationToken))
        {
            yield return block;
        }
    }

    private JsonElement BuildState()
    {
        var state = new Dictionary<string, object?>
        {
            ["thinkingEffort"] = Settings.ThinkingEffort.ToString(),
            ["temperature"] = Settings.Temperature,
            ["topP"] = Settings.TopP,
            ["topK"] = Settings.TopK,
            ["numCtx"] = Settings.NumCtx,
            ["telemetryChannel"] = _telemetry.Channel
        };
        if (ModelId is int modelId) state["modelId"] = modelId;
        return JsonSerializer.SerializeToElement(state);
    }

    public void Dispose()
    {
        _generation++;
        _activeRunId = null;
        _agent.Dispose();
    }
}

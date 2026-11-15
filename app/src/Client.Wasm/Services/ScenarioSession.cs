using AGUI.Abstractions;
using AGUI.Client;
using AGUIWebChat.Client.Formatting;
using AGUIWebChat.Client.Models;
using AGUIWebChat.Contracts.Agents;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AGUIWebChat.Client.Services;

public sealed class ScenarioSession : IAsyncDisposable
{
    private readonly BrowserAgUiBridge _bridge;
    private readonly TelemetryService _telemetry;
    private readonly ILogger _logger;
    private readonly string _route;
    private readonly Func<Action, Task> _dispatch;
    private UIAgent? _agent;
    private CancellationTokenSource? _run;
    private Task _activeRun = Task.CompletedTask;
    private readonly Dictionary<string, string> _runArguments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScenarioToolModel> _runTools = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private readonly Action<LlmTelemetryDto> _metricsHandler;
    private string _runId = "";
    private ChatSettings? _lastSettings;
    private IList<AGUIResume>? _resume;
    private int _generation;
    private bool _disposed;

    public ScenarioSession(BrowserAgUiBridge bridge, TelemetryService telemetry, ILogger logger,
        string route, Func<Action, Task> dispatch)
    {
        _bridge = bridge; _telemetry = telemetry; _logger = logger; _route = route; _dispatch = dispatch;
        Checkpoint.Route = route;
        _metricsHandler = metrics =>
        {
            int generation = _generation;
            _ = _dispatch(() =>
            {
                if (!_disposed && generation == _generation && metrics.RunId == _runId) Metrics = metrics;
            });
        };
        _telemetry.LlmMetricsCompleted += _metricsHandler;
    }

    public ScenarioCheckpoint Checkpoint { get; private set; } = new();
    public bool IsSending { get; private set; }
    public string? Error { get; private set; }
    public string? ErrorCode { get; private set; }
    public int? ErrorStatus { get; private set; }
    public bool RestartRequired { get; private set; }
    public ChatRunSnapshot? LastRun { get; private set; }
    public LlmTelemetryDto? Metrics { get; private set; }
    public bool HasPending => Checkpoint.Tools.Any(t => t.InterruptId is not null && t.Approved is null);
    public DocumentState Document => Checkpoint.State["document"]!.Deserialize(AgentServerSerializerContext.Default.DocumentState)!;
    public PlanResult? Plan => Checkpoint.State["plan"]?.Deserialize(AgentServerSerializerContext.Default.PlanResult);

    public async Task InitializeAsync(int? modelId, ChatSettings settings)
    {
        int generation = _generation;
        try { await InitializeCoreAsync(modelId, settings); }
        catch
        {
            await ClearAsync(generation);
            throw;
        }
    }

    private async Task InitializeCoreAsync(int? modelId, ChatSettings settings)
    {
        int generation = _generation;
        string? saved = await _bridge.LoadCheckpointAsync(_route);
        await _dispatch(() =>
        {
            if (_disposed || generation != _generation) throw new OperationCanceledException();
            if (saved is not null)
            {
                var restored = JsonSerializer.Deserialize(saved, ScenarioJsonContext.Default.ScenarioCheckpoint)
                    ?? throw new InvalidOperationException("Conversation checkpoint is invalid.");
                if (restored.Version != 1 || restored.Route != _route || string.IsNullOrWhiteSpace(restored.ThreadId))
                    throw new InvalidOperationException("Conversation checkpoint has no thread.");
                ValidateState(restored.State);
                _ = restored.History.Deserialize(AGUIJsonSerializerContext.Default.IListAGUIMessage)
                    ?? throw new InvalidOperationException("Conversation checkpoint has invalid history.");
                foreach (var tool in restored.Tools)
                {
                    if (string.IsNullOrWhiteSpace(tool.Id) || string.IsNullOrWhiteSpace(tool.Name))
                        throw new InvalidOperationException("Conversation checkpoint has an invalid tool.");
                    ScenarioToolProjection.ValidateArguments(tool);
                    ScenarioToolProjection.ValidateResult(tool, _route);
                }
                if (restored.Tools.Select(t => t.Key).Distinct().Count() != restored.Tools.Count ||
                    restored.Tools.Where(t => t.InterruptId is not null && t.Approved is null)
                        .Select(t => t.InterruptId).Distinct().Count() != restored.Tools.Count(t => t.InterruptId is not null && t.Approved is null))
                    throw new InvalidOperationException("Conversation checkpoint has duplicate tool identities.");
                Checkpoint = restored;
                RememberSettings(settings);
            }
            else
            {
                SetSettings(settings);
                if (modelId is not null) Checkpoint.State["modelId"] = modelId;
                Checkpoint.State["document"] = new JsonObject { ["title"] = "Untitled", ["text"] = "" };
            }
            CreateAgent();
        });
        await SaveAsync(generation);
    }

    private void CreateAgent()
    {
        int generation = _generation;
        var transport = new ScenarioAgUiTransport(_bridge, "/ag-ui/" + _route)
        {
            RunCancellation = () => _run?.Token ?? default,
            Prepare = input => _dispatch(() =>
            {
                if (_disposed || generation != _generation) throw new OperationCanceledException();
                input.ThreadId = Checkpoint.ThreadId;
                input.RunId = Guid.NewGuid().ToString("N");
                _runId = input.RunId;
                Checkpoint.State["telemetryChannel"] = _telemetry.Channel;
                input.State = JsonSerializer.SerializeToElement(Checkpoint.State, ScenarioJsonContext.Default.JsonObject);
                input.Resume = _resume;
                input.Tools = [];
                var incoming = JsonSerializer.SerializeToNode(input.Messages, AGUIJsonSerializerContext.Default.IListAGUIMessage)!.AsArray();
                var history = (JsonArray)Checkpoint.History.DeepClone();
                foreach (var item in incoming)
                {
                    if (item?["role"]?.GetValue<string>() != "user") continue;
                    string? id = item?["id"]?.GetValue<string>();
                    if (!history.Any(existing => existing?["id"]?.GetValue<string>() == id))
                        history.Add(item?.DeepClone());
                }
                Checkpoint.History = history;
                input.Messages = history.Deserialize(AGUIJsonSerializerContext.Default.IListAGUIMessage)!;
            }),
            Observe = async item =>
            {
                await _dispatch(() =>
                {
                    if (!_disposed && generation == _generation) Observe(item);
                });
                if (item is StateSnapshotEvent or StateDeltaEvent or RunFinishedEvent)
                    await SaveAsync(generation);
            }
        };
        var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = transport });
        _agent = new UIAgent(new FormattedChatClient(new ChatRunObserverClient(client, _ => { },
            snapshot => _ = _dispatch(() =>
            {
                if (!_disposed && generation == _generation)
                {
                    LastRun = snapshot;
                    Checkpoint.Reasoning = new() { Text = snapshot.Reasoning,
                        ChunkCount = snapshot.ReasoningChunkCount, DurationMs = snapshot.ReasoningDurationMs };
                }
            }))), options =>
            {
                options.ChatOptions = new ChatOptions();
            });
    }

    private void SetSettings(ChatSettings settings)
    {
        var state = Checkpoint.State;
        if (_lastSettings is null || _lastSettings.ThinkingEffort != settings.ThinkingEffort)
            state["thinkingEffort"] = settings.ThinkingEffort.ToString();
        if (_lastSettings is null || _lastSettings.Temperature != settings.Temperature) state["temperature"] = settings.Temperature;
        if (_lastSettings is null || _lastSettings.TopP != settings.TopP) state["topP"] = settings.TopP;
        if (_lastSettings is null || _lastSettings.TopK != settings.TopK) state["topK"] = settings.TopK;
        if (_lastSettings is null || _lastSettings.NumCtx != settings.NumCtx) state["numCtx"] = settings.NumCtx;
        state["telemetryChannel"] = _telemetry.Channel;
        RememberSettings(settings);
    }

    private void RememberSettings(ChatSettings settings) => _lastSettings = new()
    {
        ThinkingEffort = settings.ThinkingEffort, Temperature = settings.Temperature, TopP = settings.TopP,
        TopK = settings.TopK, NumCtx = settings.NumCtx
    };

    public async Task ChangeDocumentAsync(DocumentState document)
    {
        int generation = _generation;
        try
        {
            await _dispatch(() =>
            {
                if (!_disposed && generation == _generation)
                    Checkpoint.State["document"] = JsonSerializer.SerializeToNode(document, AgentServerSerializerContext.Default.DocumentState);
            });
            await SaveAsync(generation);
        }
        catch (Exception error)
        {
            await RecordErrorAsync(error, generation);
            await ClearAsync(generation);
        }
    }

    public async Task SendAsync(string text, ChatSettings settings)
    {
        if (IsSending || HasPending || RestartRequired || string.IsNullOrWhiteSpace(text)) return;
        int generation = _generation;
        var message = new ChatMessage(ChatRole.User, text.Trim()) { MessageId = Guid.NewGuid().ToString("N") };
        await _dispatch(() =>
        {
            if (_disposed || generation != _generation) return;
            SetSettings(settings);
            Checkpoint.Transcript.Add(new() { Id = message.MessageId, Text = text.Trim(), IsUser = true });
        });
        if (!_disposed && generation == _generation) await RunAsync(message, generation);
    }

    public async Task DecideAsync(ScenarioToolModel tool, bool approved)
    {
        int generation = _generation;
        try { await DecideCoreAsync(tool, approved); }
        catch (Exception error)
        {
            await RecordErrorAsync(error, generation);
            await ClearAsync(generation);
        }
    }

    private async Task DecideCoreAsync(ScenarioToolModel tool, bool approved)
    {
        if (IsSending || RestartRequired || tool.InterruptId is null || tool.Approved is not null || tool.Decision is not null) return;
        int generation = _generation;
        await _dispatch(() =>
        {
            if (!_disposed && generation == _generation) { tool.Decision = approved; tool.Status = "Decision staged"; }
        });
        if (_disposed || generation != _generation) return;
        var pending = Checkpoint.Tools.Where(t => t.InterruptId is not null && t.Approved is null).ToArray();
        if (pending.Any(t => t.Decision is null)) { await SaveAsync(generation); return; }
        List<AIContent> responses = [];
        _resume = pending.Select(t =>
        {
            using var arguments = JsonDocument.Parse(t.Arguments);
            responses.Add(new ToolApprovalRequestContent(t.InterruptId!,
                new FunctionCallContent(t.Id, t.Name,
                    arguments.RootElement.EnumerateObject().ToDictionary(
                        property => property.Name, property => (object?)property.Value.Clone())))
                .CreateResponse(t.Decision!.Value));
            var payload = new JsonObject
            {
                ["approved"] = t.Decision.Value,
                ["toolCall"] = new JsonObject
                {
                    ["callId"] = t.Id, ["name"] = t.Name,
                    ["arguments"] = JsonNode.Parse(arguments.RootElement.GetRawText())
                }
            };
            return new AGUIResume
            {
                InterruptId = t.InterruptId!, Status = "completed",
                Payload = JsonSerializer.SerializeToElement(payload, ScenarioJsonContext.Default.JsonObject)
            };
        }).ToList();
        try
        {
            await RunAsync(new ChatMessage(ChatRole.Tool, responses) { MessageId = Guid.NewGuid().ToString("N") }, generation);
            await _dispatch(() =>
            {
                if (Error is not null || generation != _generation || _disposed) return;
                foreach (var decision in pending)
                {
                    if (decision.Result is null || decision.Status == "Failed" || (decision.Decision == true && !decision.StateApplied))
                    {
                        Error = "The decision was not completed. Start a new conversation and request a fresh proposal.";
                        RestartRequired = true;
                        return;
                    }
                    decision.Approved = decision.Decision;
                    decision.Status = decision.Approved == true ? "Approved" : "Rejected";
                }
            });
            await SaveAsync(generation);
        }
        finally { if (generation == _generation) _resume = null; }
    }

    private async Task RunAsync(ChatMessage message, int generation)
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new List<ContentBlockChangedSubscription>();
        _run = cancellation; _activeRun = completion.Task;
        try
        {
            await _dispatch(() =>
            {
                if (_disposed || generation != _generation) throw new OperationCanceledException();
                IsSending = true; Error = null; ErrorCode = null; ErrorStatus = null;
                RestartRequired = false; LastRun = null; Metrics = null; _runId = "";
                Checkpoint.Reasoning = null; _runArguments.Clear(); _runTools.Clear();
            });
            await foreach (var block in _agent!.SendMessageAsync(message, cancellation.Token))
            {
                if (block is not RichContentBlock rich || rich.Role != ChatRole.Assistant) continue;
                void Refresh()
                {
                    if (_disposed || generation != _generation) return;
                    string key = _runId + ":" + rich.Id;
                    var item = Checkpoint.Transcript.FirstOrDefault(t => t.Id == key);
                    if (item is null) Checkpoint.Transcript.Add(item = new() { Id = key });
                    item.Text = rich.RawText;
                }
                subscriptions.Add(rich.OnChanged(() => _ = _dispatch(Refresh)));
                await _dispatch(Refresh);
            }
            await SaveAsync(generation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || generation != _generation)
        {
            await _dispatch(() =>
            {
                if (generation == _generation)
                {
                    Error = "Response stopped. Request a fresh proposal if a decision was in flight.";
                    RestartRequired = HasPending;
                }
            });
        }
        catch (Exception error)
        {
            await RecordErrorAsync(error, generation);
        }
        finally
        {
            foreach (var subscription in subscriptions) subscription.Dispose();
            await _dispatch(() =>
            {
                if (generation != _generation) return;
                IsSending = false;
                _run = null;
            });
            try
            {
                if (RestartRequired && generation == _generation)
                {
                    await ClearAsync(generation);
                }
            }
            finally { completion.SetResult(); }
        }
    }

    private void Observe(BaseEvent item)
    {
        switch (item)
        {
            case ToolCallStartEvent start:
                var existing = _resume is null ? null : Checkpoint.Tools.FirstOrDefault(t =>
                    t.Id == start.ToolCallId && t.InterruptId is not null && t.Approved is null && t.Decision is not null);
                if (existing is null)
                    existing = new() { Id = start.ToolCallId, Name = start.ToolCallName };
                else if (existing.Name != start.ToolCallName)
                    throw new InvalidOperationException("The resumed tool identity changed.");
                _runArguments[start.ToolCallId] = "";
                _runTools[start.ToolCallId] = existing;
                break;
            case ToolCallArgsEvent args:
                _runArguments[args.ToolCallId] += args.Delta;
                break;
            case ToolCallEndEvent end:
                var call = _runTools[end.ToolCallId];
                string completeArguments = _runArguments[end.ToolCallId];
                ScenarioToolProjection.ValidateArguments(new() { Name = call.Name, Arguments = completeArguments });
                if (call.InterruptId is not null && call.Arguments != completeArguments)
                    throw new InvalidOperationException("The resumed tool arguments changed.");
                call.Arguments = completeArguments;
                if (!Checkpoint.Tools.Contains(call)) Checkpoint.Tools.Add(call);
                if (Checkpoint.History.Any(m => m?["id"]?.GetValue<string>() == "call-" + call.Key)) break;
                Checkpoint.History.Add(new JsonObject
                {
                    ["id"] = "call-" + call.Key, ["role"] = "assistant",
                    ["toolCalls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = call.Id, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
                    })
                });
                break;
            case ToolCallResultEvent result:
                var tool = _runTools[result.ToolCallId];
                if (!Checkpoint.Tools.Contains(tool))
                    throw new InvalidOperationException("The tool result has no validated completed call.");
                if (result.Content.Value is not string resultText)
                    throw new InvalidOperationException("Scenario tool results must contain a JSON string or a tool error.");
                tool.Result = resultText;
                tool.Status = resultText.StartsWith("Error:", StringComparison.Ordinal) ? "Failed" : "Complete";
                try { ScenarioToolProjection.ValidateResult(tool, _route); }
                catch { tool.Status = "Failed"; tool.Result = null; throw; }
                if (tool.Status == "Failed")
                {
                    Error = "The tool could not apply this change. Start a new conversation and request a fresh proposal.";
                    RestartRequired = true;
                }
                Checkpoint.History.Add(new JsonObject
                {
                    ["id"] = _runId + ":" + result.MessageId, ["role"] = "tool",
                    ["toolCallId"] = result.ToolCallId, ["content"] = resultText
                });
                break;
            case TextMessageContentEvent text:
                string historyId = _runId + ":" + text.MessageId;
                var historyItem = Checkpoint.History.FirstOrDefault(m => m?["id"]?.GetValue<string>() == historyId);
                if (historyItem is null)
                    Checkpoint.History.Add(historyItem = new JsonObject { ["id"] = historyId, ["role"] = "assistant", ["content"] = "" });
                historyItem["content"] = historyItem["content"]!.GetValue<string>() + text.Delta;
                break;
            case StateSnapshotEvent snapshot:
                var state = JsonNode.Parse(snapshot.Snapshot.GetRawText())!.AsObject();
                ValidateState(state);
                Checkpoint.State = state;
                ConfirmState();
                break;
            case StateDeltaEvent delta:
                var candidate = ScenarioStatePatch.Apply(Checkpoint.State, delta.Delta);
                ValidateState(candidate);
                Checkpoint.State = candidate;
                ConfirmState();
                break;
            case RunErrorEvent error:
                throw new ScenarioTransportException(error.Message, error.Code ?? "agent_stream_error", true, 200);
            case RunFinishedEvent { Outcome: RunFinishedInterruptOutcome outcome }:
                foreach (var interrupt in outcome.Interrupts)
                {
                    if (string.IsNullOrWhiteSpace(interrupt.Id) || string.IsNullOrWhiteSpace(interrupt.ToolCallId) ||
                        !_runTools.TryGetValue(interrupt.ToolCallId, out var pending) || !Checkpoint.Tools.Contains(pending))
                        throw new InvalidOperationException("The agent returned an invalid tool interrupt.");
                    ScenarioToolProjection.ValidateArguments(new()
                    {
                        Name = pending.Name, Arguments = pending.Arguments, InterruptId = interrupt.Id
                    });
                }
                foreach (var interrupt in outcome.Interrupts)
                {
                    var pending = _runTools[interrupt.ToolCallId!];
                    pending.InterruptId = interrupt.Id;
                    pending.Status = "Approval required";
                }
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Checkpoint, ScenarioJsonContext.Default.ScenarioCheckpoint)) > 192 * 1024)
                    throw new ScenarioTransportException("Conversation exceeds the tab checkpoint limit. Start a new conversation.",
                        "checkpoint_limit", true, 413);
                break;
        }
    }

    private void ConfirmState()
    {
        foreach (var tool in _runTools.Values.Where(t => t.Result is not null && t.Status != "Failed"))
        {
            if (tool.Name == "switch_active_model" && Checkpoint.State["modelId"] is JsonValue model &&
                model.TryGetValue<int>(out int id) && id == ScenarioToolProjection.ModelId(tool))
                tool.StateApplied = true;
            if (tool.Name == "propose_document_edit")
            {
                var proposed = ScenarioToolProjection.ProposedDocument(tool);
                if (Document.Title == proposed.Title && Document.Text == proposed.Text) tool.StateApplied = true;
            }
        }
    }
    public static void ValidateState(JsonObject state)
    {
        if (state["document"] is not JsonObject document ||
            document["title"] is not JsonValue title || !title.TryGetValue<string>(out _) ||
            document["text"] is not JsonValue text || !text.TryGetValue<string>(out _))
            throw new InvalidOperationException("The document state must contain string title and text fields.");
        if (state["plan"] is { } planNode)
        {
            var plan = planNode.Deserialize(AgentServerSerializerContext.Default.PlanResult)
                ?? throw new InvalidOperationException("The plan state is invalid.");
            if (string.IsNullOrWhiteSpace(plan.Title) || plan.Steps.Count == 0 ||
                plan.Steps.Any(step => string.IsNullOrWhiteSpace(step.Id) || string.IsNullOrWhiteSpace(step.Title) ||
                    step.Status is not ("pending" or "active" or "done" or "blocked")) ||
                plan.Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != plan.Steps.Count)
                throw new InvalidOperationException("The plan state has invalid steps.");
        }
    }

    private async Task SaveAsync(int generation)
    {
        await _checkpointGate.WaitAsync();
        try
        {
            string? saved = null;
            await _dispatch(() =>
            {
                if (!_disposed && generation == _generation && !RestartRequired)
                    saved = JsonSerializer.Serialize(Checkpoint, ScenarioJsonContext.Default.ScenarioCheckpoint);
            });
            if (saved is not null) await _bridge.SaveCheckpointAsync(_route, saved);
        }
        finally { _checkpointGate.Release(); }
    }

    private async Task ClearAsync(int generation)
    {
        await _checkpointGate.WaitAsync();
        try { if (!_disposed && generation == _generation) await _bridge.ClearCheckpointAsync(_route); }
        finally { _checkpointGate.Release(); }
    }

    private Task RecordErrorAsync(Exception error, int generation)
    {
        _logger.LogWarning(error, "Scenario operation failed on {Route}.", _route);
        return _dispatch(() =>
        {
            if (_disposed || generation != _generation) return;
            Error = error is ScenarioTransportException ? error.Message :
                "The agent request could not be completed. Start a new conversation and request a fresh proposal.";
            ErrorCode = error is ScenarioTransportException transport ? transport.Code : "scenario_operation";
            ErrorStatus = error is ScenarioTransportException status ? status.Status : null;
            RestartRequired = error is ScenarioTransportException restart ? restart.RestartRequired : true;
        });
    }

    public void Stop() => _run?.Cancel();

    public async Task ResetAsync(int? modelId, ChatSettings settings)
    {
        int generation = 0;
        await _dispatch(() => { generation = ++_generation; Stop(); });
        await _activeRun;
        if (_disposed || generation != _generation) throw new OperationCanceledException();
        _agent?.Dispose();
        await ClearAsync(generation);
        await _dispatch(() =>
        {
            if (_disposed || generation != _generation) throw new OperationCanceledException();
            Checkpoint = new()
            {
                Route = _route,
                State = new JsonObject { ["document"] = new JsonObject { ["title"] = "Untitled", ["text"] = "" } }
            };
            _runArguments.Clear(); _runTools.Clear(); _resume = null;
            Error = null; ErrorCode = null; ErrorStatus = null; RestartRequired = false;
            IsSending = false; LastRun = null; Metrics = null; _runId = ""; _lastSettings = null; _run = null;
        });
        await InitializeAsync(modelId, settings);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true; _generation++;
        _telemetry.LlmMetricsCompleted -= _metricsHandler;
        Stop();
        await _activeRun;
        _agent?.Dispose();
    }
}

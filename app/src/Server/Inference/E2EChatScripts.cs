using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace AGUIWebChat.Server.Inference;

public sealed record E2EChatStep(string MessageId, string? Text = null, bool WaitForRelease = false,
    string? Error = null, string? Tool = null, JsonElement? Arguments = null, string? Reasoning = null);

public sealed class E2EChatScripts : IDisposable
{
    public const string PromptPrefix = "e2e-script:";
    private readonly ConcurrentDictionary<string, Script> _scripts = new(StringComparer.Ordinal);

    public string Add(IReadOnlyList<E2EChatStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        string id = Guid.NewGuid().ToString("N");
        _scripts[id] = new Script(steps.ToArray());
        return id;
    }

    public bool Release(string id)
    {
        if (!_scripts.TryGetValue(id, out var script)) return false;
        return script.Release();
    }

    public bool Remove(string id)
    {
        if (!_scripts.TryRemove(id, out var script)) return false;
        script.Cancel();
        return true;
    }

    public object? Status(string id) => _scripts.TryGetValue(id, out var script) ? script.Status : null;

    public IAsyncEnumerable<ChatResponseUpdate> RunAsync(string id, CancellationToken cancellationToken,
        IEnumerable<ChatMessage>? history = null)
    {
        if (!_scripts.TryGetValue(id, out var script))
        {
            throw new InvalidOperationException($"E2E chat script '{id}' does not exist.");
        }

        return script.RunAsync(cancellationToken, history);
    }

    public void Dispose()
    {
        foreach (var id in _scripts.Keys) Remove(id);
    }

    private sealed class Script
    {
        private readonly IReadOnlyList<E2EChatStep> _steps;
        private readonly Channel<bool> _release = Channel.CreateBounded<bool>(1);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly CancellationToken _cancellationToken;
        private int _started;
        private int _active;
        private int _position;
        private volatile bool _awaitingTool;
        private volatile bool _cancelled;
        private int _calls;
        private readonly ConcurrentDictionary<string, string> _results = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ownedCalls = new(StringComparer.Ordinal);
        public object Status => new { calls = Volatile.Read(ref _calls), cancelled = _cancelled, results = _results.Values.ToArray() };

        public Script(IReadOnlyList<E2EChatStep> steps)
        {
            _steps = steps;
            _cancellationToken = _cancellation.Token;
        }

        public bool Release() => _release.Writer.TryWrite(true);

        public void Cancel()
        {
            _cancellation.Cancel();
            _release.Writer.TryComplete();
            _cancellation.Dispose();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> RunAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken, IEnumerable<ChatMessage>? history)
        {
            if (Interlocked.Exchange(ref _active, 1) != 0)
                throw new InvalidOperationException("Each E2E chat script can run only once.");
            try
            {
                if (Interlocked.Exchange(ref _started, 1) != 0 && !_awaitingTool)
                    throw new InvalidOperationException("Each E2E chat script can run only once.");
                _awaitingTool = false;
                Interlocked.Increment(ref _calls);
                foreach (var result in history?.SelectMany(m => m.Contents).OfType<FunctionResultContent>() ?? [])
                    if (_ownedCalls.Contains(result.CallId)) _results[result.CallId] = result.Result?.ToString() ?? string.Empty;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationToken);
                while (_position < _steps.Count)
                {
                    var step = _steps[_position++];
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (step.WaitForRelease) await _release.Reader.ReadAsync(linked.Token);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        _cancelled = true;
                        throw;
                    }
                    if (step.Error is not null) throw new InvalidOperationException(step.Error);
                    if (step.Text is not null)
                        yield return new ChatResponseUpdate(ChatRole.Assistant, step.Text) { MessageId = step.MessageId };
                    if (step.Reasoning is not null)
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(step.Reasoning)])
                        { MessageId = step.MessageId };
                    if (step.Tool is not null)
                    {
                        var arguments = step.Arguments?.EnumerateObject().ToDictionary(
                            property => property.Name, property => (object?)property.Value.Clone()) ?? [];
                        string callId = $"script-call-{_position}";
                        _ownedCalls.Add(callId);
                        _awaitingTool = true;
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(callId, step.Tool, arguments)])
                        { MessageId = step.MessageId, FinishReason = ChatFinishReason.ToolCalls };
                        yield break;
                    }
                }
            }
            finally { Volatile.Write(ref _active, 0); }
        }
    }
}

using AGUI.Abstractions;
using AGUI.Client;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AGUIWebChat.Client.Services;

public sealed class ScenarioTransportException(string message, string code, bool restartRequired, int status)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public bool RestartRequired { get; } = restartRequired;
    public int Status { get; } = status;
}

public sealed class ScenarioAgUiTransport(BrowserAgUiBridge bridge, string path) : IAGUITransport
{
    public Func<RunAgentInput, Task>? Prepare { get; set; }
    public Func<BaseEvent, Task>? Observe { get; set; }
    public Func<CancellationToken>? RunCancellation { get; set; }

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, RunCancellation?.Invoke() ?? default);
        cancellationToken = linked.Token;
        if (Prepare is not null) await Prepare(input);
        using var response = await bridge.SendAsync(path,
            JsonSerializer.SerializeToUtf8Bytes(input, AGUIJsonSerializerContext.Default.RunAgentInput), cancellationToken);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using var bytes = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = await responseStream.ReadAsync(chunk, cancellationToken)) != 0)
            {
                if (bytes.Length + count > 16 * 1024)
                    throw new ScenarioTransportException("The agent returned an oversized error response.", "agent_error_limit", true, (int)response.StatusCode);
                bytes.Write(chunk, 0, count);
            }
            JsonDocument document;
            try { document = JsonDocument.Parse(bytes.ToArray()); }
            catch (JsonException)
            {
                throw new ScenarioTransportException("The agent returned an invalid error response.", "agent_error_format", true, (int)response.StatusCode);
            }
            using var parsed = document;
            var error = document.RootElement;
            throw new ScenarioTransportException(
                error.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "Agent request failed." :
                    error.TryGetProperty("title", out var title) ? title.GetString() ?? "Agent request failed." : "Agent request failed.",
                error.TryGetProperty("code", out var code) ? code.GetString() ?? "agent_http_error" : "agent_http_error",
                error.TryGetProperty("restartRequired", out var restart) && restart.ValueKind == JsonValueKind.True,
                (int)response.StatusCode);
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new ScenarioTransportException("The agent returned an unsupported response.", "agent_content_type", true, (int)response.StatusCode);
        bool started = false, finished = false;
        await foreach (var item in response.ReadAGUIEventStreamAsync(cancellationToken))
        {
            if (finished || (item is RunStartedEvent && started) ||
                (!started && item is not (RunStartedEvent or RunErrorEvent)))
                throw new ScenarioTransportException("The agent returned an invalid execution sequence.",
                    "agent_stream_sequence", true, (int)response.StatusCode);
            if (item is RunStartedEvent start)
            {
                if (start.ThreadId != input.ThreadId || start.RunId != input.RunId)
                    throw new ScenarioTransportException("The agent returned a different execution identity.",
                        "agent_stream_identity", true, (int)response.StatusCode);
                started = true;
            }
            if (item is RunFinishedEvent end)
            {
                if (end.ThreadId != input.ThreadId || end.RunId != input.RunId)
                    throw new ScenarioTransportException("The agent returned a different execution identity.",
                        "agent_stream_identity", true, (int)response.StatusCode);
                finished = true;
            }
            if (Observe is not null) await Observe(item);
            if (item is RunErrorEvent error)
                throw new ScenarioTransportException(error.Message, error.Code ?? "agent_stream_error", true, (int)response.StatusCode);
            yield return item;
        }
        if (!finished)
            throw new ScenarioTransportException("The agent stream ended before execution completed. Start a new conversation.",
                "agent_stream_incomplete", true, (int)response.StatusCode);
    }
}

namespace AGUIWebChat.Client.Services;

/// <summary>
/// Snapshot of the in-flight AG-UI run as observed by <see cref="ChatRunObserverClient"/>.
/// </summary>
/// <param name="RunId">AG-UI run id from the <c>RunStartedEvent</c>, if seen yet.</param>
/// <param name="Reasoning">Accumulated reasoning text for this run.</param>
/// <param name="ReasoningChunkCount">Number of reasoning chunks received.</param>
/// <param name="ReasoningDurationMs">Elapsed milliseconds since the first reasoning chunk.</param>
public sealed record ChatRunSnapshot(
    string? RunId,
    string Reasoning,
    int ReasoningChunkCount,
    double ReasoningDurationMs);

using AGUIWebChat.Server.Hubs;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Serilog;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AGUIWebChat.Server.Telemetry;

static class ReasoningTelemetry
{
    public const string ActivitySourceName = "AGUIWebChat.Server.ReasoningTelemetry";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Serilog.ILogger Logger = Log.ForContext("SourceContext", ActivitySourceName);

    public static async IAsyncEnumerable<AgentResponseUpdate> ObserveAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        IHubContext<TelemetryHub> telemetryHub,
        string? telemetryChannel,
        string runId,
        string agentName,
        string modelName,
        string thinkingLevel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Activity? activity = null;
        var duration = new Stopwatch();
        int chunks = 0;
        int characters = 0;
        try
        {
            await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
            {
                bool containsReasoning = false;
                foreach (var content in update.Contents)
                {
                    if (content is not TextReasoningContent reasoning) continue;
                    containsReasoning = true;
                    if (chunks == 0)
                    {
                        activity = ActivitySource.StartActivity("gen_ai.reasoning", ActivityKind.Internal);
                        activity?.SetTag("gen_ai.reasoning.effort", thinkingLevel);
                        activity?.SetTag("gen_ai.request.model", modelName);
                        activity?.SetTag("gen_ai.agent.name", agentName);
                        activity?.SetTag("gen_ai.run.id", runId);
                    }
                    duration.Start();
                    chunks++;
                    characters += reasoning.Text?.Length ?? 0;
                }
                if (!containsReasoning) duration.Stop();
                yield return update;
            }
        }
        finally
        {
            duration.Stop();
            if (chunks > 0)
            {
                activity?.SetTag("gen_ai.reasoning.chunk_count", chunks);
                activity?.SetTag("gen_ai.reasoning.character_count", characters);
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.Stop();
                activity?.Dispose();

                Logger.Debug("Reasoning completed RunId={RunId} Agent={AgentName} Model={ModelName} Effort={ThinkingLevel} Chunks={ChunkCount} Characters={CharacterCount} DurationMs={DurationMs}",
                    runId, agentName, modelName, thinkingLevel, chunks, characters, duration.Elapsed.TotalMilliseconds);

                // SignalR statistics must not depend on tracing listeners or sampling.
                await TelemetryPublisher.SendAsync(telemetryHub, telemetryChannel, "ReasoningCompleted",
                    new ReasoningTelemetryDto(runId, duration.Elapsed.TotalMilliseconds, chunks, characters),
                    cancellationToken);
            }
        }
    }
}

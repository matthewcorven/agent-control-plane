using AGUI.Abstractions;
using AGUI.Server;
using AGUIWebChat.Server.Telemetry;
using AGUIWebChat.Server.Hubs;
using AGUIWebChat.Server.Inference;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Serilog;
using System.Text.Json;


namespace AGUIWebChat.Server.Agents;

internal static class ChatAgentFactory
{
    public static AIAgent CreateAgent(
        IChatClient chatClient,
        string name,
        string instruction,
        string source,
        string modelName,
        InferenceSettings defaultSettings,
        IHubContext<TelemetryHub> telemetryHub,
        ProviderInferenceCapabilities? capabilities = null,
        IList<AITool>? tools = null,
        bool requestOwnedHistory = false)
    {
        capabilities ??= ProviderInferenceCapabilities.For(ModelProviderKind.Ollama);

        Log.Debug("SERVER AGENT_CREATION for {AgentName} with ActivitySource {ActivitySource}", name, source);

        ChatOptions defaultChatOptions =
            InferenceSettingsParser.ApplyInferenceSettings(null, defaultSettings, capabilities).ChatOptions!;
        defaultChatOptions.Instructions = instruction;

        if (tools is { Count: > 0 })
        {
            defaultChatOptions.Tools = tools;
        }

        return chatClient
            .AsAIAgent(
                new ChatClientAgentOptions
                {
                    Name = name,
                    // AG-UI resume reconstructs approval messages; storing them again duplicates call IDs.
                    ChatHistoryProvider = requestOwnedHistory ? new InMemoryChatHistoryProvider(new()
                    {
                        StorageInputRequestMessageFilter = _ => [],
                        StorageInputResponseMessageFilter = _ => []
                    }) : null,
                    ChatOptions = defaultChatOptions
                })
            .AsBuilder()
            .UseOpenTelemetry(sourceName: source)
            .Use(runFunc: null,
                runStreamingFunc:
                    (messages, session, options, innerAgent, cancellationToken) =>
                    {
                        var effectiveSettings =
                            InferenceSettingsParser.GetInferenceSettings(
                                options,
                                defaultSettings);

                        var effectiveOptions =
                            InferenceSettingsParser.ApplyInferenceSettings(
                                options,
                                effectiveSettings,
                                capabilities);

                        Log.Information("SERVER INFERENCE_SETTINGS Agent={AgentName} Model={ModelName} Effort={ThinkingEffort} Temperature={Temperature} TopP={TopP} TopK={TopK} NumCtx={NumCtx} Dropped={Dropped}",
                            name,
                            modelName,
                            effectiveSettings.ThinkingEffort,
                            effectiveSettings.Temperature,
                            effectiveSettings.TopP,
                            effectiveSettings.TopK,
                            effectiveSettings.NumCtx,
                            capabilities.DroppedSettings());

                        string runId = string.Empty;
                        string? telemetryChannel = null;

                        if (effectiveOptions is ChatClientAgentRunOptions { ChatOptions: { } chatOptions } &&
                            chatOptions.TryGetRunAgentInput(out RunAgentInput? input))
                        {
                            runId = input.RunId;
                            if (input.State is { ValueKind: JsonValueKind.Object } state && state.TryGetProperty("telemetryChannel", out var channel) && channel.ValueKind == JsonValueKind.String && Guid.TryParseExact(channel.GetString(), "N", out _)) telemetryChannel = channel.GetString();
                        }

                        var reasoningStream =
                           ReasoningTelemetry.ObserveAsync(
                               messages,
                               session,
                               effectiveOptions,
                               innerAgent,
                               telemetryHub,
                               telemetryChannel,
                               runId,
                               name,
                               modelName,
                               effectiveSettings.ThinkingEffort,
                               cancellationToken);

                        return LlmTelemetry.ObserveAsync(
                            reasoningStream,
                            telemetryHub,
                               telemetryChannel,
                            runId,
                            name,
                            modelName,
                            cancellationToken,
                            capabilities.Provider.ToString());
                    })
            .Build();
    }

}

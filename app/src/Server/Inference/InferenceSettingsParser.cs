using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace AGUIWebChat.Server.Inference;

public static class InferenceSettingsParser
{
    public static InferenceSettings GetInferenceSettings(AgentRunOptions? options, InferenceSettings defaults)
    {
        if (options is not ChatClientAgentRunOptions
            {
                ChatOptions: { } chatOptions
            })
        {
            return defaults;
        }

        if (!chatOptions.TryGetRunAgentInput(out RunAgentInput? input))
        {
            return defaults;
        }

        if (input.State is not
            {
                ValueKind: JsonValueKind.Object
            } state)
        {
            return defaults;
        }

        return Parse(state, defaults);
    }

    public static InferenceSettings Parse(JsonElement state, InferenceSettings defaults)
    {
        if (state.ValueKind != JsonValueKind.Object)
            return defaults;

        string thinkingEffort = defaults.ThinkingEffort;
        double temperature = defaults.Temperature;
        double topP = defaults.TopP;
        int topK = defaults.TopK;
        int numCtx = defaults.NumCtx;

        if (state.TryGetProperty("thinkingEffort", out var thinkingElement) && thinkingElement.ValueKind == JsonValueKind.String)
        {
            thinkingEffort = thinkingElement.GetString()?.ToLowerInvariant() switch
            {
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                _ => defaults.ThinkingEffort
            };
        }

        if (state.TryGetProperty("temperature", out var temperatureElement) &&
            temperatureElement.ValueKind == JsonValueKind.Number &&
            temperatureElement.TryGetDouble(out var parsedTemperature) &&
            parsedTemperature is >= 0 and <= 2)
        {
            temperature = parsedTemperature;
        }

        if (state.TryGetProperty("topP", out var topPElement) &&
            topPElement.ValueKind == JsonValueKind.Number &&
            topPElement.TryGetDouble(out var parsedTopP) &&
            parsedTopP is >= 0 and <= 1)
        {
            topP = parsedTopP;
        }

        if (state.TryGetProperty("topK", out var topKElement) &&
            topKElement.ValueKind == JsonValueKind.Number &&
            topKElement.TryGetInt32(out var parsedTopK) &&
            parsedTopK is >= 1 and <= 1000)
        {
            topK = parsedTopK;
        }

        if (state.TryGetProperty("numCtx", out var numCtxElement) &&
            numCtxElement.ValueKind == JsonValueKind.Number &&
            numCtxElement.TryGetInt32(out var parsedNumCtx) &&
            parsedNumCtx is >= 1024 and <= 131072)
        {
            numCtx = parsedNumCtx;
        }

        return new InferenceSettings
        {
            ThinkingEffort = thinkingEffort,
            Temperature = temperature,
            TopP = topP,
            TopK = topK,
            NumCtx = numCtx
        };
    }

    /// <summary>
    /// Applies <paramref name="settings"/> to the run options, keeping only the knobs the target
    /// provider understands. Ollama-only fields are dropped for other providers so a single
    /// settings payload can drive any backend.
    /// </summary>
    public static ChatClientAgentRunOptions ApplyInferenceSettings(
        AgentRunOptions? options,
        InferenceSettings settings,
        ProviderInferenceCapabilities capabilities)
    {
        ChatClientAgentRunOptions runOptions;

        if (options is ChatClientAgentRunOptions chatClientOptions)
        {
            runOptions = (ChatClientAgentRunOptions)chatClientOptions.Clone();
        }
        else
        {
            runOptions = new ChatClientAgentRunOptions();
        }

        runOptions.ChatOptions ??= new ChatOptions();
        runOptions.ChatOptions.AdditionalProperties ??= new();

        // A reused run can contain options from a different selected model.
        foreach (string key in new[] { "temperature", "top_p", "think", "top_k", "num_ctx" })
        {
            runOptions.ChatOptions.AdditionalProperties.Remove(key);
        }

        foreach (KeyValuePair<string, object?> property in BuildAdditionalProperties(settings, capabilities))
        {
            runOptions.ChatOptions.AdditionalProperties[property.Key] = property.Value;
        }

        runOptions.ChatOptions.Temperature = capabilities.SupportsSampling ? (float)settings.Temperature : null;
        runOptions.ChatOptions.TopP = capabilities.SupportsSampling ? (float)settings.TopP : null;
        runOptions.ChatOptions.Reasoning = capabilities.SupportsReasoning
            ? new ReasoningOptions
            {
                Output = ReasoningOutput.Full,
                Effort = ToReasoningEffort(settings.ThinkingEffort)
            }
            : null;

        return runOptions;
    }

    /// <summary>
    /// Maps the shared thinking-effort vocabulary onto the provider-neutral reasoning effort.
    /// </summary>
    public static ReasoningEffort ToReasoningEffort(string? thinkingEffort) => thinkingEffort?.ToLowerInvariant() switch
    {
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        _ => ReasoningEffort.Medium
    };

    /// <summary>
    /// Builds the provider-neutral plus provider-specific inference properties for
    /// <paramref name="settings"/>, omitting anything <paramref name="capabilities"/> excludes.
    /// </summary>
    public static AdditionalPropertiesDictionary BuildAdditionalProperties(
        InferenceSettings settings,
        ProviderInferenceCapabilities capabilities)
    {
        AdditionalPropertiesDictionary properties = new();

        if (capabilities.SupportsSampling)
        {
            properties["temperature"] = settings.Temperature;
            properties["top_p"] = settings.TopP;
        }

        if (capabilities.SupportsThinkingEffort)
        {
            properties["think"] = settings.ThinkingEffort;
        }

        if (capabilities.SupportsTopK)
        {
            properties["top_k"] = settings.TopK;
        }

        if (capabilities.SupportsNumCtx)
        {
            properties["num_ctx"] = settings.NumCtx;
        }

        return properties;
    }

}

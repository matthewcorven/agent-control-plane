namespace AGUIWebChat.Server.Inference;

/// <summary>
/// Which inference knobs a selected model actually honours. Knobs a provider does not understand are
/// dropped before the request is sent, so one shared <see cref="InferenceSettings"/> payload can
/// drive any provider without leaking Ollama-only fields into a Foundry request.
/// </summary>
public sealed record ProviderInferenceCapabilities(
    bool SupportsThinkingEffort,
    bool SupportsTopK,
    bool SupportsNumCtx,
    bool SupportsReasoning = false,
    bool SupportsSampling = true)
{
    public ModelProviderKind Provider { get; init; }

    public static ProviderInferenceCapabilities For(
        ModelProviderKind kind,
        bool supportsReasoning = false) => (kind switch
    {
        ModelProviderKind.Ollama => new ProviderInferenceCapabilities(
            SupportsThinkingEffort: true,
            SupportsTopK: true,
            SupportsNumCtx: true),

        // Use the selected deployment's API surface, not its name or provider alone.
        ModelProviderKind.Foundry => new ProviderInferenceCapabilities(
            SupportsThinkingEffort: false,
            SupportsTopK: false,
            SupportsNumCtx: false,
            SupportsReasoning: supportsReasoning,
            SupportsSampling: !supportsReasoning),

        _ => new ProviderInferenceCapabilities(
            SupportsThinkingEffort: false,
            SupportsTopK: false,
            SupportsNumCtx: false)
    }) with { Provider = kind };

    /// <summary>Names of the settings this provider will drop, for logging.</summary>
    public IReadOnlyList<string> DroppedSettings()
    {
        List<string> dropped = [];

        if (!SupportsThinkingEffort) dropped.Add("think");
        if (!SupportsTopK) dropped.Add("top_k");
        if (!SupportsNumCtx) dropped.Add("num_ctx");
        if (!SupportsSampling) dropped.Add("temperature");
        if (!SupportsSampling) dropped.Add("top_p");

        return dropped;
    }
}

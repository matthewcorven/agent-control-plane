namespace AGUIWebChat.Server.Inference;

/// <summary>
/// The inference backends the server can build an <c>IChatClient</c> for. The values match the
/// <c>AIProvider.ProviderType</c> column so a catalogue row maps straight onto a factory branch.
/// </summary>
public enum ModelProviderKind
{
    /// <summary>The provider type is not recognised; only provider-neutral settings are applied.</summary>
    Unknown = 0,

    /// <summary>Ollama over HTTP. Supports the full knob set, including <c>think</c> and <c>num_ctx</c>.</summary>
    Ollama,

    /// <summary>Microsoft Foundry (Azure OpenAI). Supports the standard sampling knobs only.</summary>
    Foundry,

    /// <summary>Local oMLX OpenAI-compatible chat completions.</summary>
    Omlx
}

public static class ModelProviderKindExtensions
{
    public static ModelProviderKind ParseProviderKind(this string? providerType) =>
        Enum.TryParse(providerType, ignoreCase: true, out ModelProviderKind kind)
            ? kind
            : ModelProviderKind.Unknown;
}

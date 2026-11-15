namespace AGUIWebChat.Server.Inference;

/// <summary>Raised when the requested model or provider cannot produce a chat client.</summary>
public abstract class ModelProviderException(string message) : InvalidOperationException(message);

public sealed class ModelNotFoundException(int modelId)
    : ModelProviderException($"AI model with id {modelId} was not found.");

public sealed class ProviderDisabledException(string providerName)
    : ModelProviderException($"AI provider '{providerName}' is disabled.");

public sealed class UnsupportedProviderException(string providerType)
    : ModelProviderException($"AI provider type '{providerType}' is not supported.");

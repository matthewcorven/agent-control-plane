namespace AGUIWebChat.Server.Inference;

/// <summary>
/// Raised when a Foundry model is selected but the endpoint is missing or malformed. This is a
/// configuration error, not a provider outage, so it is reported before any network call.
/// </summary>
public sealed class FoundryNotConfiguredException(string message) : ModelProviderException(message);

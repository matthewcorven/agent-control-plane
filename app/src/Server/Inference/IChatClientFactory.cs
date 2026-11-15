using Microsoft.Extensions.AI;

namespace AGUIWebChat.Server.Inference;

/// <summary>The client, catalogue model id (when one was used) and its provider capabilities.</summary>
public sealed record DefaultChatClient(
    IChatClient Client,
    int? ModelId,
    ProviderInferenceCapabilities Capabilities,
    string? ModelName = null);

/// <summary>
/// Builds the <see cref="IChatClient"/> for a catalogue model. Callers wrap the returned client in
/// <c>ChatAgentFactory.CreateAgent</c>, so every provider gets the same MAF middleware pipeline
/// (telemetry, reasoning, settings) with only the inner transport swapped.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Resolves the catalogue row and returns a client for its provider.
    /// </summary>
    /// <exception cref="ModelNotFoundException">The model id is not in the catalogue.</exception>
    /// <exception cref="ProviderDisabledException">The model or its provider is disabled.</exception>
    /// <exception cref="UnsupportedProviderException">The provider type has no client yet.</exception>
    Task<IChatClient> CreateAsync(int modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The capabilities of the selected model and its API surface, used to drop
    /// settings the model does not support.
    /// </summary>
    Task<ProviderInferenceCapabilities> GetCapabilitiesAsync(int modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the model id of the first enabled catalogue model, or <c>null</c> when none is
    /// available. Used as the startup default when the client has not chosen a model.
    /// </summary>
    Task<int?> GetDefaultModelIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the client for the first enabled catalogue model. When the catalogue is empty the
    /// factory falls back to the configured <c>OLLAMA_MODEL</c>, which keeps the pre-catalogue
    /// behaviour for deployments that never seeded a model row.
    /// </summary>
    Task<DefaultChatClient> CreateDefaultAsync(CancellationToken cancellationToken = default);
}

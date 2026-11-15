using AGUIWebChat.Server.Data;
using AGUIWebChat.Server.Domain.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Serilog;

namespace AGUIWebChat.Server.Inference;

/// <summary>
/// Builds provider-specific <see cref="IChatClient"/> instances from the AI model catalogue.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly ChatDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly Func<IChatClient> _stubFactory;
    private readonly Func<string, IChatClient> _ollamaFactory;
    private readonly FoundryChatClientFactory _foundryFactory;

    public ChatClientFactory(
        ChatDbContext dbContext,
        IConfiguration configuration,
        Func<IChatClient>? stubFactory = null,
        Func<string, IChatClient>? ollamaFactory = null,
        FoundryChatClientFactory? foundryFactory = null)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _stubFactory = stubFactory ?? (() => new ScriptedChatClient());
        _ollamaFactory = ollamaFactory ?? CreateOllamaClient;
        _foundryFactory = foundryFactory ?? new FoundryChatClientFactory(FoundryOptions.Resolve(configuration));
    }

    public async Task<IChatClient> CreateAsync(int modelId, CancellationToken cancellationToken = default)
    {
        var (model, kind) = await ResolveAsync(modelId, cancellationToken);

        // E2E runs replace every provider with a deterministic echo client.
        if (_configuration.GetValue<bool>("E2E:StubInference"))
        {
            return _stubFactory();
        }

        return kind switch
        {
            ModelProviderKind.Ollama => _ollamaFactory(model.ModelId),

            ModelProviderKind.Omlx => new OmlxChatClientFactory(_configuration).Create(model.ModelId, model.Provider.Endpoint),

            ModelProviderKind.Foundry => _foundryFactory.Create(
                model.ModelId,
                model.Provider.Endpoint,
                FoundryChatClientFactory.RequiresResponsesApi(model)),

            _ => throw new UnsupportedProviderException(model.Provider.ProviderType)
        };
    }

    public async Task<ProviderInferenceCapabilities> GetCapabilitiesAsync(
        int modelId,
        CancellationToken cancellationToken = default)
    {
        var (model, kind) = await ResolveAsync(modelId, cancellationToken);

        return ProviderInferenceCapabilities.For(
            kind,
            kind == ModelProviderKind.Foundry && FoundryChatClientFactory.RequiresResponsesApi(model));
    }

    public async Task<int?> GetDefaultModelIdAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _dbContext.AIModels
                .AsNoTracking()
                .Where(model => model.IsEnabled && model.Provider.IsEnabled)
                .OrderBy(model => model.Id)
                .Select(model => (int?)model.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (DatabaseSanityCheck.IsCatalogueUnavailable(exception))
        {
            // An absent catalogue has the same meaning as an empty one: fall back to the
            // configured Foundry deployment or OLLAMA_MODEL. Startup already logged the full
            // diagnostic, so this stays at warning level.
            Log.Warning(
                "The AI model catalogue is unavailable ({Error}); falling back to configured inference.",
                exception.Message);

            return null;
        }
    }

    public async Task<DefaultChatClient> CreateDefaultAsync(CancellationToken cancellationToken = default)
    {
        int? modelId = await GetDefaultModelIdAsync(cancellationToken);

        if (modelId is int id)
        {
            IChatClient client = await CreateAsync(id, cancellationToken);
            ProviderInferenceCapabilities capabilities = await GetCapabilitiesAsync(id, cancellationToken);

            string modelName = await _dbContext.AIModels.Where(model => model.Id == id)
                .Select(model => model.ModelId).SingleAsync(cancellationToken);
            return new DefaultChatClient(client, id, capabilities, modelName);
        }

        // Explicit oMLX fallback is opt-in and only applies when no catalogue model is enabled.
        string? omlxModel = _configuration["OMLX_MODEL"];
        if (!string.IsNullOrWhiteSpace(omlxModel))
        {
            IChatClient omlx = _configuration.GetValue<bool>("E2E:StubInference")
                ? _stubFactory()
                : new OmlxChatClientFactory(_configuration).Create(omlxModel);
            return new DefaultChatClient(omlx, null, ProviderInferenceCapabilities.For(ModelProviderKind.Omlx), omlxModel);
        }

        // No catalogue rows. A configured Foundry deployment wins over the Ollama fallback so
        // FOUNDRY_MODEL / FOUNDRY_REASONING_MODEL are usable before the catalogue is seeded.
        FoundryOptions foundryOptions = _foundryFactory.Options;

        if (foundryOptions.IsConfigured && !string.IsNullOrWhiteSpace(foundryOptions.Model ?? foundryOptions.ReasoningModel))
        {
            bool useResponsesApi = !string.IsNullOrWhiteSpace(foundryOptions.ReasoningModel);
            string deployment = useResponsesApi ? foundryOptions.ReasoningModel! : foundryOptions.Model!;

            Log.Warning(
                "No enabled AI model is configured; falling back to the Foundry deployment {Deployment}.",
                deployment);

            return new DefaultChatClient(
                _foundryFactory.Create(deployment, catalogueEndpoint: null, useResponsesApi),
                null,
                ProviderInferenceCapabilities.For(ModelProviderKind.Foundry, useResponsesApi),
                deployment);
        }

        // Otherwise behave exactly as the pre-catalogue server did.
        Log.Warning("No enabled AI model is configured; falling back to OLLAMA_MODEL.");

        IChatClient fallback = _configuration.GetValue<bool>("E2E:StubInference")
            ? _stubFactory()
            : _ollamaFactory(_configuration["OLLAMA_MODEL"] ?? "qwen3:1.7b");

        return new DefaultChatClient(
            fallback,
            null,
            ProviderInferenceCapabilities.For(ModelProviderKind.Ollama),
            _configuration["OLLAMA_MODEL"] ?? "qwen3:1.7b");
    }

    private async Task<(AIModel Model, ModelProviderKind Kind)> ResolveAsync(
        int modelId,
        CancellationToken cancellationToken)
    {
        var model = await _dbContext.AIModels
            .AsNoTracking()
            .Include(x => x.Provider)
            .Include(x => x.ReasoningEfforts)
            .FirstOrDefaultAsync(x => x.Id == modelId, cancellationToken)
            ?? throw new ModelNotFoundException(modelId);

        if (!model.IsEnabled)
        {
            throw new ProviderDisabledException($"{model.Provider.Name}/{model.ModelId}");
        }

        if (!model.Provider.IsEnabled)
        {
            throw new ProviderDisabledException(model.Provider.Name);
        }

        var kind = model.Provider.ProviderType.ParseProviderKind();

        if (kind == ModelProviderKind.Foundry)
        {
            ValidateFoundryProvider(model.Provider);
        }

        Log.Debug("SERVER PROVIDER_RESOLUTION Model={ModelId} Provider={ProviderName} ProviderType={ProviderType} Kind={Kind}",
            model.ModelId,
            model.Provider.Name,
            model.Provider.ProviderType,
            kind);

        return (model, kind);
    }

    /// <summary>
    /// A Foundry provider row must carry an https endpoint. Validating here means a
    /// misconfigured catalogue fails at selection time with a clear message instead of an
    /// opaque Azure SDK error mid-stream.
    /// </summary>
    private static void ValidateFoundryProvider(AIProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            return;
        }

        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new FoundryNotConfiguredException(
                $"Provider '{provider.Name}' has endpoint '{provider.Endpoint}', which is not an absolute https URI.");
        }
    }

    private IChatClient CreateOllamaClient(string modelId)
    {
        // OLLAMA_ENDPOINT is the application's own setting. The Aspire AppHost injects
        // OLLAMA_HOST for the Ollama executable resource, so fall back to it when running
        // under orchestration.
        var endpoint = _configuration["OLLAMA_ENDPOINT"]
            ?? _configuration["OLLAMA_HOST"]
            ?? throw new InvalidOperationException("OLLAMA_ENDPOINT is not set.");

        var handler = new OllamaHttpLoggingHandler { InnerHandler = new HttpClientHandler() };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(endpoint) };

        return new OllamaApiClient(httpClient, modelId);
    }
}

using AGUIWebChat.Contracts.Agents;
using AGUIWebChat.Server.Services.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AGUIWebChat.Server.Inference;
using Serilog;

namespace AGUIWebChat.Server.Agents.Tools;

/// <summary>
/// Scenario tools that read the model catalogue. Every tool resolves
/// <see cref="IAIModelService"/> from a fresh scope because the service is
/// registered scoped and the agent outlives any single request scope.
/// </summary>
internal static class ModelCatalogTools
{
    public const string ListAvailableModelsName = "list_available_models";
    public const string GetModelDetailsName = "get_model_details";
    public const string SwitchActiveModelName = "switch_active_model";

    public static IList<AITool> CreateReadOnlyTools(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        return
        [
            AIFunctionFactory.Create(
                (CancellationToken cancellationToken) => ListAvailableModelsAsync(scopeFactory, cancellationToken),
                new AIFunctionFactoryOptions
                {
                    Name = ListAvailableModelsName,
                    Description = "List the AI models that are currently enabled and selectable.",
                    SerializerOptions = AgentServerSerializerContext.Default.Options
                }),
            AIFunctionFactory.Create(
                (int modelId, CancellationToken cancellationToken) => GetModelDetailsAsync(scopeFactory, modelId, cancellationToken),
                new AIFunctionFactoryOptions
                {
                    Name = GetModelDetailsName,
                    Description = "Get the full catalogue details for one AI model by its numeric id.",
                    SerializerOptions = AgentServerSerializerContext.Default.Options
                })
        ];
    }

    /// <summary>
    /// The consequential tool. Callers wrap it in an approval requirement so the
    /// user must confirm before the active model changes.
    /// </summary>
    public static AIFunction CreateSwitchActiveModelFunction(IServiceScopeFactory scopeFactory, ScenarioState state)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        return AIFunctionFactory.Create(
            (int modelId, CancellationToken cancellationToken) => SwitchActiveModelAsync(scopeFactory, state, modelId, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = SwitchActiveModelName,
                Description = "Select an enabled tool-capable model for this conversation's next run. Requires user approval.",
                SerializerOptions = AgentServerSerializerContext.Default.Options
            });
    }

    private static async Task<ModelCatalogResult> ListAvailableModelsAsync(
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IAIModelService modelService = scope.ServiceProvider.GetRequiredService<IAIModelService>();

        IReadOnlyList<Contracts.AI.Models.AIModelEditModel> models =
            await modelService.GetModelsAsync(enabledOnly: true, cancellationToken);

        return new ModelCatalogResult
        {
            TotalCount = models.Count,
            Models = models
                .Select(model => new ModelCatalogEntry
                {
                    Id = model.Id,
                    ModelId = model.ModelId,
                    DisplayName = model.DisplayName,
                    ProviderName = model.ProviderName,
                    SupportsTools = model.SupportsTools,
                    SupportsVision = model.SupportsVision,
                    ContextWindow = model.ContextWindow
                })
                .ToList()
        };
    }

    private static async Task<ModelDetailsResult> GetModelDetailsAsync(
        IServiceScopeFactory scopeFactory,
        int modelId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IAIModelService modelService = scope.ServiceProvider.GetRequiredService<IAIModelService>();

        Contracts.AI.Models.AIModelEditModel? model =
            await modelService.GetModelAsync(modelId, cancellationToken);

        if (model is null)
        {
            Log.Information("get_model_details: model {ModelId} was not found", modelId);
            return new ModelDetailsResult { Found = false, Id = modelId };
        }

        return new ModelDetailsResult
        {
            Found = true,
            Id = model.Id,
            ModelId = model.ModelId,
            DisplayName = model.DisplayName,
            ProviderName = model.ProviderName,
            ThinkingMode = model.ThinkingMode.ToString(),
            SupportsTools = model.SupportsTools,
            SupportsVision = model.SupportsVision,
            SupportsStreaming = model.SupportsStreaming,
            ContextWindow = model.ContextWindow,
            MaxOutputTokens = model.MaxOutputTokens,
            ReasoningEfforts = model.ReasoningEfforts.Select(effort => effort.Value).ToList()
        };
    }

    private static async Task<ModelSwitchResult> SwitchActiveModelAsync(
        IServiceScopeFactory scopeFactory,
        ScenarioState state,
        int modelId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IAIModelService modelService = scope.ServiceProvider.GetRequiredService<IAIModelService>();

        Contracts.AI.Models.AIModelEditModel? model =
            await modelService.GetModelAsync(modelId, cancellationToken);

        await scope.ServiceProvider.GetRequiredService<IChatClientFactory>()
            .GetCapabilitiesAsync(modelId, cancellationToken);

        if (model is null || !model.SupportsTools)
        {
            Log.Warning("Rejected conversation model switch to {ModelId}: model lacks tool support", modelId);
            throw new ArgumentException("The selected scenario model must support tools.");
        }

        return new ModelSwitchResult
        {
            Switched = true,
            ModelId = model.Id,
            DisplayName = model.DisplayName,
            Message = $"Selected {model.DisplayName} for this conversation's next run.",
            Changes = state.Replace("modelId", model.Id, AgentServerSerializerContext.Default.Int32)
        };
    }
}

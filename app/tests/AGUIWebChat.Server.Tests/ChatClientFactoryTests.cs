using AGUIWebChat.Server.Data;
using AGUIWebChat.Server.Domain.AI;
using AGUIWebChat.Server.Inference;
using AGUIWebChat.Server.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class ChatClientFactoryTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();

    private static async Task<(TestDbContextFactory Factory, int ModelId)> SeedAsync(
        string providerType,
        bool providerEnabled = true,
        bool modelEnabled = true,
        string? endpoint = null,
        Contracts.AI.ThinkingMode thinkingMode = Contracts.AI.ThinkingMode.None)
    {
        var factory = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);

        var provider = new AIProvider
        {
            Name = $"{providerType} provider",
            ProviderType = providerType,
            Endpoint = endpoint,
            IsEnabled = providerEnabled
        };

        factory.DbContext.AIProviders.Add(provider);
        await factory.DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var model = new AIModel
        {
            ProviderId = provider.Id,
            ModelId = "test-model",
            DisplayName = "Test Model",
            ThinkingMode = thinkingMode,
            IsEnabled = modelEnabled
        };

        factory.DbContext.AIModels.Add(model);
        await factory.DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (factory, model.Id);
    }

    [Fact]
    public async Task OllamaProviderBuildsClientFromCatalogue()
    {
        var (factory, modelId) = await SeedAsync("Ollama");
        await using var _ = factory;

        string? requestedModel = null;
        var client = new ChatClientFactory(
            factory.DbContext,
            Configuration(),
            ollamaFactory: model => { requestedModel = model; return new ScriptedChatClient(); });

        var result = await client.CreateAsync(modelId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("test-model", requestedModel);
    }

    [Fact]
    public async Task OmlxProviderUsesCatalogueModelAndCapabilities()
    {
        var (factory, modelId) = await SeedAsync("oMLX", endpoint: "http://127.0.0.1:8001/v1");
        await using var _ = factory;
        var client = new ChatClientFactory(factory.DbContext, Configuration());
        using var result = await client.CreateAsync(modelId, TestContext.Current.CancellationToken);
        Assert.Equal("test-model", result.GetService<ChatClientMetadata>()?.DefaultModelId);
        var capabilities = await client.GetCapabilitiesAsync(modelId, TestContext.Current.CancellationToken);
        Assert.Equal(ModelProviderKind.Omlx, capabilities.Provider);
        Assert.False(capabilities.SupportsTopK);
        Assert.False(capabilities.SupportsNumCtx);
    }

    [Fact]
    public async Task OmlxCatalogueRejectsRemoteEndpointEvenWithSafeConfiguredEndpoint()
    {
        var (factory, modelId) = await SeedAsync("Omlx", endpoint: "http://example.com/v1");
        await using var _ = factory;
        var client = new ChatClientFactory(factory.DbContext, Configuration(("OMLX_ENDPOINT", "http://127.0.0.1:8001/v1")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateAsync(modelId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnabledCatalogueModelTakesPrecedenceOverOmlxFallback()
    {
        var (factory, modelId) = await SeedAsync("Ollama");
        await using var _ = factory;
        var client = new ChatClientFactory(factory.DbContext, Configuration(("OMLX_MODEL", "local-model")),
            ollamaFactory: _ => new ScriptedChatClient());
        var result = await client.CreateDefaultAsync(TestContext.Current.CancellationToken);
        using var chat = result.Client;
        Assert.Equal(modelId, result.ModelId);
        Assert.Equal(ModelProviderKind.Ollama, result.Capabilities.Provider);
    }

    [Fact]
    public async Task EmptyCatalogueUsesExplicitOmlxFallback()
    {
        var factory = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);
        await using var _ = factory;
        var client = new ChatClientFactory(factory.DbContext, Configuration(("OMLX_MODEL", "local-model")));
        var result = await client.CreateDefaultAsync(TestContext.Current.CancellationToken);
        using var chat = result.Client;
        Assert.Null(result.ModelId);
        Assert.Equal("local-model", result.ModelName);
        Assert.Equal(ModelProviderKind.Omlx, result.Capabilities.Provider);
    }

    [Fact]
    public async Task FoundryProviderBuildsClientFromCatalogue()
    {
        var (factory, modelId) = await SeedAsync("Foundry", endpoint: "https://example.openai.azure.com/");
        await using var _ = factory;

        Uri? requestedEndpoint = null;
        var foundry = new FoundryChatClientFactory(
            new FoundryOptions(null, null, null),
            endpoint => { requestedEndpoint = endpoint; return FakeAzureClient(); });

        var client = new ChatClientFactory(factory.DbContext, Configuration(), foundryFactory: foundry);

        var result = await client.CreateAsync(modelId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(new Uri("https://example.openai.azure.com/"), requestedEndpoint);
    }

    [Fact]
    public async Task FoundryProviderWithoutEndpointIsRejected()
    {
        var (factory, modelId) = await SeedAsync("Foundry");
        await using var _ = factory;

        var foundry = new FoundryChatClientFactory(
            new FoundryOptions(null, null, null),
            _ => FakeAzureClient());

        var client = new ChatClientFactory(factory.DbContext, Configuration(), foundryFactory: foundry);

        var error = await Assert.ThrowsAsync<FoundryNotConfiguredException>(
            () => client.CreateAsync(modelId, TestContext.Current.CancellationToken));

        Assert.Contains("endpoint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FoundryProviderWithNonHttpsEndpointIsRejected()
    {
        var (factory, modelId) = await SeedAsync("Foundry", endpoint: "http://example.openai.azure.com/");
        await using var _ = factory;

        var client = new ChatClientFactory(factory.DbContext, Configuration());

        await Assert.ThrowsAsync<FoundryNotConfiguredException>(
            () => client.CreateAsync(modelId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FoundryReasoningModelUsesResponsesSurface()
    {
        var (factory, modelId) = await SeedAsync(
            "Foundry",
            endpoint: "https://example.openai.azure.com/",
            thinkingMode: Contracts.AI.ThinkingMode.Effort);
        await using var _ = factory;

        var foundry = new FoundryChatClientFactory(
            new FoundryOptions(null, null, null),
            _ => FakeAzureClient());

        var client = new ChatClientFactory(factory.DbContext, Configuration(), foundryFactory: foundry);

        var result = await client.CreateAsync(modelId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData(Contracts.AI.ThinkingMode.None, false)]
    [InlineData(Contracts.AI.ThinkingMode.OnOff, true)]
    [InlineData(Contracts.AI.ThinkingMode.Effort, true)]
    public async Task FoundryCapabilitiesFollowSelectedThinkingMode(
        Contracts.AI.ThinkingMode thinkingMode,
        bool supportsReasoning)
    {
        var (factory, modelId) = await SeedAsync(
            "Foundry", endpoint: "https://example.openai.azure.com/", thinkingMode: thinkingMode);
        await using var _ = factory;

        var client = new ChatClientFactory(factory.DbContext, Configuration());
        var capabilities = await client.GetCapabilitiesAsync(modelId, TestContext.Current.CancellationToken);

        Assert.Equal(supportsReasoning, capabilities.SupportsReasoning);
        Assert.False(capabilities.SupportsNumCtx);
    }

    [Fact]
    public async Task EmptyCatalogueFallsBackToConfiguredFoundryDeployment()
    {
        var factory = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);
        await using var _ = factory;

        var foundry = new FoundryChatClientFactory(
            new FoundryOptions("https://example.openai.azure.com/", "gpt-4o", "o4-mini"),
            _ => FakeAzureClient());

        var client = new ChatClientFactory(factory.DbContext, Configuration(), foundryFactory: foundry);

        var result = await client.CreateDefaultAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.ModelId);
        Assert.True(result.Capabilities.SupportsReasoning);
    }

    [Fact]
    public async Task EmptyCatalogueWithoutFoundryFallsBackToOllama()
    {
        var factory = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);
        await using var _ = factory;

        var client = new ChatClientFactory(
            factory.DbContext,
            Configuration(("OLLAMA_MODEL", "fallback-model")),
            ollamaFactory: _ => new ScriptedChatClient());

        var result = await client.CreateDefaultAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.ModelId);
        Assert.False(result.Capabilities.SupportsReasoning);
    }

    /// <summary>
    /// Constructing an <see cref="AzureOpenAIClient"/> with a key credential performs no network
    /// I/O, so this stands in for a real Foundry resource without needing Azure access.
    /// </summary>
    private static Azure.AI.OpenAI.AzureOpenAIClient FakeAzureClient() =>
        new(new Uri("https://example.openai.azure.com/"), new Azure.AzureKeyCredential("test-key"));

    [Fact]
    public async Task UnknownProviderTypeIsRejected()
    {
        var (factory, modelId) = await SeedAsync("SomethingElse");
        await using var _ = factory;

        var client = new ChatClientFactory(factory.DbContext, Configuration());

        await Assert.ThrowsAsync<UnsupportedProviderException>(
            () => client.CreateAsync(modelId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisabledProviderIsRejected()
    {
        var (factory, modelId) = await SeedAsync("Ollama", providerEnabled: false);
        await using var _ = factory;

        var client = new ChatClientFactory(factory.DbContext, Configuration());

        await Assert.ThrowsAsync<ProviderDisabledException>(
            () => client.CreateAsync(modelId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisabledModelIsRejected()
    {
        var (factory, modelId) = await SeedAsync("Ollama", modelEnabled: false);
        await using var _ = factory;

        var client = new ChatClientFactory(factory.DbContext, Configuration());

        await Assert.ThrowsAsync<ProviderDisabledException>(
            () => client.CreateAsync(modelId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingModelIsRejected()
    {
        var (factory, _) = await SeedAsync("Ollama");
        await using var __ = factory;

        var client = new ChatClientFactory(factory.DbContext, Configuration());

        await Assert.ThrowsAsync<ModelNotFoundException>(
            () => client.CreateAsync(9999, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StubInferenceOverridesEveryProvider()
    {
        var (factory, modelId) = await SeedAsync("Foundry");
        await using var _ = factory;

        var client = new ChatClientFactory(
            factory.DbContext,
            Configuration(("E2E:StubInference", "true")));

        var result = await client.CreateAsync(modelId, TestContext.Current.CancellationToken);

        Assert.IsType<ScriptedChatClient>(result);
    }

    [Fact]
    public async Task CapabilitiesMatchProviderKind()
    {
        var (factory, modelId) = await SeedAsync("Ollama");
        await using var _ = factory;

        var client = new ChatClientFactory(factory.DbContext, Configuration());
        var capabilities = await client.GetCapabilitiesAsync(modelId, TestContext.Current.CancellationToken);

        Assert.True(capabilities.SupportsThinkingEffort);
        Assert.True(capabilities.SupportsTopK);
        Assert.True(capabilities.SupportsNumCtx);
        Assert.Empty(capabilities.DroppedSettings());
    }

    [Fact]
    public async Task DefaultModelSkipsDisabledProviders()
    {
        var (factory, _) = await SeedAsync("Ollama", providerEnabled: false);
        await using var __ = factory;

        var client = new ChatClientFactory(factory.DbContext, Configuration());

        Assert.Null(await client.GetDefaultModelIdAsync(TestContext.Current.CancellationToken));
    }
}

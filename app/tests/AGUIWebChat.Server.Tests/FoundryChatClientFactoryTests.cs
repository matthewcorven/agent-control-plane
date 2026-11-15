using AGUIWebChat.Contracts.AI;
using AGUIWebChat.Server.Domain.AI;
using AGUIWebChat.Server.Inference;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class FoundryChatClientFactoryTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();

    [Theory]
    [InlineData(ThinkingMode.None, false)]
    [InlineData(ThinkingMode.OnOff, true)]
    [InlineData(ThinkingMode.Effort, true)]
    public void RequiresResponsesApiFollowsThinkingMode(ThinkingMode thinkingMode, bool expected)
    {
        var model = new AIModel { ModelId = "deployment", ThinkingMode = thinkingMode };

        Assert.Equal(expected, FoundryChatClientFactory.RequiresResponsesApi(model));
    }

    [Fact]
    public void RequiresResponsesApiWhenReasoningEffortsAreDeclared()
    {
        var model = new AIModel
        {
            ModelId = "deployment",
            ThinkingMode = ThinkingMode.None,
            ReasoningEfforts = [new AIModelReasoningEffort { DisplayName = "High", Value = "high" }]
        };

        Assert.True(FoundryChatClientFactory.RequiresResponsesApi(model));
    }

    [Fact]
    public void ResolveEndpointPrefersCatalogueEndpoint()
    {
        var options = new FoundryOptions("https://config.openai.azure.com/", null, null);

        Assert.Equal(
            new Uri("https://catalogue.openai.azure.com/"),
            options.ResolveEndpoint("https://catalogue.openai.azure.com/"));
    }

    [Fact]
    public void ResolveEndpointFallsBackToConfiguration()
    {
        var options = new FoundryOptions("https://config.openai.azure.com/", null, null);

        Assert.Equal(new Uri("https://config.openai.azure.com/"), options.ResolveEndpoint(null));
    }

    [Fact]
    public void ResolveEndpointThrowsWhenNothingIsConfigured()
    {
        var options = new FoundryOptions(null, null, null);

        Assert.Throws<FoundryNotConfiguredException>(() => options.ResolveEndpoint(null));
    }

    [Theory]
    [InlineData("http://insecure.openai.azure.com/")]
    [InlineData("not-a-uri")]
    public void ResolveEndpointRejectsNonHttpsEndpoints(string endpoint)
    {
        var options = new FoundryOptions(null, null, null);

        Assert.Throws<FoundryNotConfiguredException>(() => options.ResolveEndpoint(endpoint));
    }

    [Fact]
    public void EnvironmentVariablesWinOverConfigurationSection()
    {
        var configuration = Configuration(
            ("FOUNDRY_URI", "https://env.openai.azure.com/"),
            ("FOUNDRY_MODEL", "env-model"),
            ("FOUNDRY_REASONING_MODEL", "env-reasoning"),
            ("Foundry:Endpoint", "https://config.openai.azure.com/"),
            ("Foundry:Model", "config-model"),
            ("Foundry:ReasoningModel", "config-reasoning"));

        var options = FoundryOptions.Resolve(configuration);

        Assert.Equal("https://env.openai.azure.com/", options.Endpoint);
        Assert.Equal("env-model", options.Model);
        Assert.Equal("env-reasoning", options.ReasoningModel);
    }

    [Fact]
    public void ConfigurationSectionIsUsedWhenEnvironmentIsAbsent()
    {
        var configuration = Configuration(
            ("Foundry:Endpoint", "https://config.openai.azure.com/"),
            ("Foundry:Model", "config-model"));

        var options = FoundryOptions.Resolve(configuration);

        Assert.Equal("https://config.openai.azure.com/", options.Endpoint);
        Assert.Equal("config-model", options.Model);
        Assert.Null(options.ReasoningModel);
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void UnconfiguredOptionsReportNotConfigured()
    {
        Assert.False(FoundryOptions.Resolve(Configuration()).IsConfigured);
    }
}

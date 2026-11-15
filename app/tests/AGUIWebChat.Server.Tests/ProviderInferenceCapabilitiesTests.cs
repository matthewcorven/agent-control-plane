using AGUIWebChat.Server.Inference;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class ProviderInferenceCapabilitiesTests
{
    [Fact]
    public void OllamaKeepsEveryKnob()
    {
        var properties = InferenceSettingsParser.BuildAdditionalProperties(
            new InferenceSettings(),
            ProviderInferenceCapabilities.For(ModelProviderKind.Ollama));

        Assert.Equal(
            ["num_ctx", "temperature", "think", "top_k", "top_p"],
            properties.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void FoundryDropsOllamaOnlyKnobs()
    {
        var properties = InferenceSettingsParser.BuildAdditionalProperties(
            new InferenceSettings(),
            ProviderInferenceCapabilities.For(ModelProviderKind.Foundry));

        Assert.Equal(
            ["temperature", "top_p"],
            properties.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void UnknownProviderDropsOllamaOnlyKnobs()
    {
        var capabilities = ProviderInferenceCapabilities.For(ModelProviderKind.Unknown);

        Assert.Equal(["think", "top_k", "num_ctx"], capabilities.DroppedSettings());
    }

    [Fact]
    public void OnlySelectedFoundryReasoningModelsSupportReasoning()
    {
        Assert.False(ProviderInferenceCapabilities.For(ModelProviderKind.Foundry).SupportsReasoning);
        Assert.True(ProviderInferenceCapabilities.For(ModelProviderKind.Foundry, supportsReasoning: true).SupportsReasoning);
        Assert.False(ProviderInferenceCapabilities.For(ModelProviderKind.Ollama).SupportsReasoning);
        Assert.False(ProviderInferenceCapabilities.For(ModelProviderKind.Unknown).SupportsReasoning);
    }

    [Fact]
    public void FoundryRunOptionsCarryReasoning()
    {
        var runOptions = InferenceSettingsParser.ApplyInferenceSettings(
            null,
            new InferenceSettings { ThinkingEffort = "high" },
            ProviderInferenceCapabilities.For(ModelProviderKind.Foundry, supportsReasoning: true));

        var chatOptions = Assert.IsType<Microsoft.Agents.AI.ChatClientAgentRunOptions>(runOptions).ChatOptions;

        Assert.NotNull(chatOptions?.Reasoning);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.High, chatOptions!.Reasoning!.Effort);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningOutput.Full, chatOptions.Reasoning.Output);
    }

    [Fact]
    public void OllamaRunOptionsCarryNoReasoning()
    {
        var runOptions = InferenceSettingsParser.ApplyInferenceSettings(
            null,
            new InferenceSettings(),
            ProviderInferenceCapabilities.For(ModelProviderKind.Ollama));

        var chatOptions = Assert.IsType<Microsoft.Agents.AI.ChatClientAgentRunOptions>(runOptions).ChatOptions;

        Assert.Null(chatOptions?.Reasoning);
        Assert.Equal((float)new InferenceSettings().Temperature, chatOptions?.Temperature);
        Assert.Equal((float)new InferenceSettings().TopP, chatOptions?.TopP);
    }

    [Fact]
    public void FoundryReasoningDropsSamplingAndReportsIt()
    {
        var capabilities = ProviderInferenceCapabilities.For(ModelProviderKind.Foundry, supportsReasoning: true);
        var options = InferenceSettingsParser.ApplyInferenceSettings(null, new(), capabilities).ChatOptions;

        Assert.False(capabilities.SupportsSampling);
        Assert.Null(options!.Temperature);
        Assert.Null(options.TopP);
        Assert.Empty(options.AdditionalProperties!);
        Assert.Equal(["think", "top_k", "num_ctx", "temperature", "top_p"], capabilities.DroppedSettings());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyingFoundrySettingsClearsStaleOwnedOptionsWithoutMutatingCaller(bool reasoning)
    {
        var original = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                Instructions = "keep instructions",
                Temperature = 1.5f,
                TopP = 0.5f,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
                AdditionalProperties = new()
                {
                    ["think"] = "low",
                    ["top_k"] = 5,
                    ["num_ctx"] = 1024,
                    ["temperature"] = 1.5,
                    ["top_p"] = 0.5,
                    ["unrelated"] = "keep me"
                }
            }
        };
        var settings = new InferenceSettings { ThinkingEffort = "high", Temperature = 0.25, TopP = 0.75 };
        var capabilities = ProviderInferenceCapabilities.For(ModelProviderKind.Foundry, reasoning);

        var applied = InferenceSettingsParser.ApplyInferenceSettings(original, settings, capabilities).ChatOptions!;

        Assert.NotSame(original.ChatOptions, applied);
        Assert.NotSame(original.ChatOptions.AdditionalProperties, applied.AdditionalProperties);
        Assert.Equal("keep instructions", applied.Instructions);
        Assert.Equal("keep me", applied.AdditionalProperties!["unrelated"]);
        Assert.False(applied.AdditionalProperties.ContainsKey("think"));
        Assert.False(applied.AdditionalProperties.ContainsKey("top_k"));
        Assert.False(applied.AdditionalProperties.ContainsKey("num_ctx"));
        if (reasoning)
        {
            Assert.Null(applied.Temperature);
            Assert.Null(applied.TopP);
            Assert.Equal(ReasoningEffort.High, applied.Reasoning!.Effort);
            Assert.Equal(["unrelated"], applied.AdditionalProperties.Keys);
        }
        else
        {
            Assert.Equal(0.25f, applied.Temperature);
            Assert.Equal(0.75f, applied.TopP);
            Assert.Null(applied.Reasoning);
        }
        Assert.Equal(1.5f, original.ChatOptions.Temperature);
        Assert.Equal(0.5f, original.ChatOptions.TopP);
        Assert.Equal(ReasoningEffort.Low, original.ChatOptions.Reasoning.Effort);
        Assert.Equal(6, original.ChatOptions.AdditionalProperties.Count);
    }

    [Theory]
    [InlineData("low", Microsoft.Extensions.AI.ReasoningEffort.Low)]
    [InlineData("medium", Microsoft.Extensions.AI.ReasoningEffort.Medium)]
    [InlineData("high", Microsoft.Extensions.AI.ReasoningEffort.High)]
    [InlineData("HIGH", Microsoft.Extensions.AI.ReasoningEffort.High)]
    [InlineData(null, Microsoft.Extensions.AI.ReasoningEffort.Medium)]
    [InlineData("nonsense", Microsoft.Extensions.AI.ReasoningEffort.Medium)]
    public void ThinkingEffortMapsToReasoningEffort(string? thinkingEffort, Microsoft.Extensions.AI.ReasoningEffort expected)
    {
        Assert.Equal(expected, InferenceSettingsParser.ToReasoningEffort(thinkingEffort));
    }

    [Theory]
    [InlineData("Ollama", ModelProviderKind.Ollama)]
    [InlineData("ollama", ModelProviderKind.Ollama)]
    [InlineData("Foundry", ModelProviderKind.Foundry)]
    [InlineData("nonsense", ModelProviderKind.Unknown)]
    [InlineData(null, ModelProviderKind.Unknown)]
    public void ProviderTypeParsesToKind(string? providerType, ModelProviderKind expected)
    {
        Assert.Equal(expected, providerType.ParseProviderKind());
    }
}

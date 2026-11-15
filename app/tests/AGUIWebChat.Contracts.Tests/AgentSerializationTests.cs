using System.Text.Json;
using AGUIWebChat.Contracts.Agents;
using Xunit;

namespace AGUIWebChat.Contracts.Tests;

public class AgentSerializationTests
{
    [Fact]
    public void ToolResultsUseSharedGeneratedMetadata()
    {
        var context = AgentServerSerializerContext.Default;
        var result = new ModelCatalogResult
        {
            TotalCount = 1,
            Models = [new ModelCatalogEntry { Id = 7, ModelId = "local", DisplayName = "Local", SupportsTools = true }]
        };

        string json = JsonSerializer.Serialize(result, context.ModelCatalogResult);
        Assert.Contains("\"totalCount\":1", json);
        Assert.Contains("\"supportsTools\":true", json);
        var restored = JsonSerializer.Deserialize(json, context.ModelCatalogResult)!;
        Assert.Equal(7, Assert.Single(restored.Models).Id);
        Assert.Equal(1, restored.TotalCount);
    }

    [Fact]
    public void DocumentPlanAndPatchRoundTripWithoutReflectionResolver()
    {
        var context = AgentServerSerializerContext.Default;
        var document = new DocumentState { Title = "Title", Text = "Text" };
        Assert.Equal("Text", JsonSerializer.Deserialize(
            JsonSerializer.Serialize(document, context.DocumentState), context.DocumentState)!.Text);
        var plan = new PlanResult { Title = "Plan", Steps = [new PlanStep { Id = "step-1", Title = "Build", Status = "active" }] };
        Assert.Equal("active", JsonSerializer.Deserialize(
            JsonSerializer.Serialize(plan, context.PlanResult), context.PlanResult)!.Steps[0].Status);
        JsonPatchOperation[] changes =
        [
            new() { Op = "replace", Path = "/plan/steps/0/status", Value = JsonSerializer.SerializeToElement("done", context.String) }
        ];
        string json = JsonSerializer.Serialize(changes, context.JsonPatchOperationArray);
        using var patch = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, patch.RootElement.ValueKind);
        Assert.Equal("/plan/steps/0/status", patch.RootElement[0].GetProperty("path").GetString());
        Assert.Equal("done", JsonSerializer.Deserialize(json, context.JsonPatchOperationArray)![0].Value.GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void ModelDetailsGeneratedMetadataRetainsEveryCapabilityAndOptionalBoundary(int? limit)
    {
        var context = AgentServerSerializerContext.Default;
        var details = new ModelDetailsResult
        {
            Found = true, Id = 7, ModelId = "deployment", DisplayName = "Selected model",
            ProviderName = "Foundry", ThinkingMode = "Effort", SupportsTools = true,
            SupportsVision = true, SupportsStreaming = true, ContextWindow = limit,
            MaxOutputTokens = limit, ReasoningEfforts = ["low", "high"]
        };
        string json = JsonSerializer.Serialize(details, context.ModelDetailsResult);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string[] fields =
        [
            "found", "id", "modelId", "displayName", "providerName", "thinkingMode",
            "supportsTools", "supportsVision", "supportsStreaming", "reasoningEfforts"
        ];
        if (limit is not null) fields = [.. fields, "contextWindow", "maxOutputTokens"];
        Assert.Equal(fields.Order(), root.EnumerateObject().Select(property => property.Name).Order());
        var restored = JsonSerializer.Deserialize(json, context.ModelDetailsResult)!;
        Assert.True(restored.Found);
        Assert.Equal(7, restored.Id);
        Assert.Equal("deployment", restored.ModelId);
        Assert.Equal("Selected model", restored.DisplayName);
        Assert.Equal("Foundry", restored.ProviderName);
        Assert.Equal("Effort", restored.ThinkingMode);
        Assert.True(restored.SupportsTools);
        Assert.True(restored.SupportsVision);
        Assert.True(restored.SupportsStreaming);
        Assert.Equal(limit, restored.ContextWindow);
        Assert.Equal(limit, restored.MaxOutputTokens);
        Assert.Equal(["low", "high"], restored.ReasoningEfforts);
    }

    [Fact]
    public void DefaultToolResultsKeepEmptyCollectionsAndPendingStatusInGeneratedMetadata()
    {
        var context = AgentServerSerializerContext.Default;
        var selection = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new ModelSwitchResult(), context.ModelSwitchResult), context.ModelSwitchResult)!;
        Assert.False(selection.Switched);
        Assert.Equal(0, selection.ModelId);
        Assert.Empty(selection.Changes);
        Assert.Empty(selection.DisplayName);
        Assert.Empty(selection.Message);
        var update = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new PlanStepUpdate { Id = "step-1" }, context.PlanStepUpdate), context.PlanStepUpdate)!;
        Assert.Equal("step-1", update.Id);
        Assert.Equal("pending", update.Status);
        var plan = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new PlanResult(), context.PlanResult), context.PlanResult)!;
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void DocumentAndExplicitNullPatchValueRemainExactThroughGeneratedMetadata(string text)
    {
        var context = AgentServerSerializerContext.Default;
        var document = new DocumentState { Title = " Exact title ", Text = text };
        var restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(document, context.DocumentState), context.DocumentState)!;
        Assert.Equal(document.Title, restored.Title);
        Assert.Equal(text, restored.Text);

        JsonPatchOperation[] changes =
        [
            new() { Op = "add", Path = "/optional", Value = JsonSerializer.SerializeToElement<object?>(null) }
        ];
        string json = JsonSerializer.Serialize(changes, context.JsonPatchOperationArray);
        using var patch = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, patch.RootElement[0].GetProperty("value").ValueKind);
        var operation = Assert.Single(JsonSerializer.Deserialize(json, context.JsonPatchOperationArray)!);
        Assert.Equal("add", operation.Op);
        Assert.Equal("/optional", operation.Path);
        Assert.Equal(JsonValueKind.Null, operation.Value.ValueKind);
    }
}

using AGUIWebChat.Client.Formatting;
using AGUIWebChat.Client.Models;
using System.Text.Json;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class ScenarioToolProjectionTests
{
    [Theory]
    [InlineData("switch_active_model", "{}")]
    [InlineData("switch_active_model", "[]")]
    [InlineData("switch_active_model", """{"modelId":null}""")]
    [InlineData("switch_active_model", """{"modelId":"1"}""")]
    [InlineData("switch_active_model", """{"modelId":true}""")]
    [InlineData("switch_active_model", """{"modelId":0}""")]
    [InlineData("switch_active_model", """{"modelId":-1}""")]
    [InlineData("switch_active_model", """{"modelId":1.5}""")]
    [InlineData("switch_active_model", """{"modelId":2147483648}""")]
    [InlineData("propose_document_edit", "{}")]
    [InlineData("propose_document_edit", """{"text":"","originalTitle":"","originalText":""}""")]
    [InlineData("propose_document_edit", """{"title":"Valid","originalTitle":"","originalText":""}""")]
    [InlineData("propose_document_edit", """{"title":"Valid","text":"","originalText":""}""")]
    [InlineData("propose_document_edit", """{"title":"Valid","text":"","originalTitle":""}""")]
    [InlineData("propose_document_edit", """{"title":" ","text":"","originalTitle":"","originalText":""}""")]
    [InlineData("propose_document_edit", """{"title":null,"text":"","originalTitle":"","originalText":""}""")]
    [InlineData("propose_document_edit", """{"title":"Valid","text":null,"originalTitle":"","originalText":""}""")]
    [InlineData("propose_document_edit", """{"title":"Valid","text":"","originalTitle":7,"originalText":""}""")]
    [InlineData("propose_document_edit", """{"title":"Valid","text":"","originalTitle":"","originalText":[]}""")]
    public void SemanticPendingArgumentsFailBeforeResultValidation(string name, string arguments)
    {
        var tool = new ScenarioToolModel { Name = name, Arguments = arguments, InterruptId = "pending" };
        Assert.Null(tool.Result);
        Assert.Throws<JsonException>(() => ScenarioToolProjection.ValidateArguments(tool));
        Assert.Equal(arguments, tool.Arguments);
        Assert.Null(tool.Result);
        Assert.Null(tool.Approved);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void ApprovalArgumentsRemainPositiveInt32AndOpaque(int id)
    {
        string arguments = $$"""{ "modelId":{{id}}, "future":{"opaque":[true," "]} }""";
        var tool = new ScenarioToolModel { Name = "switch_active_model", Arguments = arguments };
        ScenarioToolProjection.ValidateArguments(tool);
        Assert.Equal(id, ScenarioToolProjection.ModelId(tool));
        Assert.Equal(arguments, tool.Arguments);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData(" \t\n", " \t", " \t\n")]
    public void PredictiveArgumentsRetainExactEmptyOrWhitespaceDocumentStrings(string text, string originalTitle, string originalText)
    {
        string arguments = JsonSerializer.Serialize(new { title = " Proposed ", text, originalTitle, originalText });
        var tool = new ScenarioToolModel { Name = "propose_document_edit", Arguments = arguments };
        ScenarioToolProjection.ValidateArguments(tool);
        var document = ScenarioToolProjection.ProposedDocument(tool);
        Assert.Equal(" Proposed ", document.Title);
        Assert.Equal(text, document.Text);
        Assert.Equal(arguments, tool.Arguments);
    }

    [Theory]
    [InlineData("list_available_models", "{}")]
    [InlineData("list_available_models", """{"models":null,"totalCount":0}""")]
    [InlineData("list_available_models", """{"models":[],"totalCount":1}""")]
    [InlineData("get_model_details", "{}")]
    [InlineData("switch_active_model", "{}")]
    [InlineData("switch_active_model", """{"switched":false,"modelId":1,"changes":[]}""")]
    public void MissingTypedResultsAreErrorsNotSuccessShapedDefaults(string name, string result) =>
        Assert.ThrowsAny<Exception>(() => ScenarioToolProjection.ValidateResult(new() { Name = name, Result = result }));

    [Theory]
    [InlineData("harness-skills", "load_skill")]
    [InlineData("harness-memory", "file_memory_write")]
    [InlineData("harness-planning", "todos_add")]
    [InlineData("harness-planning", "todos_complete")]
    [InlineData("harness-planning", "todos_remove")]
    [InlineData("harness-planning", "todos_get_remaining")]
    [InlineData("harness-planning", "todos_get_all")]
    [InlineData("harness-planning", "mode_set")]
    [InlineData("harness-planning", "mode_get")]
    [InlineData("harness-assistant", "todos_get_all")]
    [InlineData("harness-assistant", "meal_ideas")]
    public void HarnessResultsAreRestrictedToTheirOwnRouteAndBounded(string route, string name)
    {
        var tool = new ScenarioToolModel { Name = name, Result = "result" };
        ScenarioToolProjection.ValidateResult(tool, route);
        Assert.Throws<InvalidOperationException>(() => ScenarioToolProjection.ValidateResult(tool, "tools"));
        tool.Result = new string('x', 131073);
        Assert.Throws<InvalidOperationException>(() => ScenarioToolProjection.ValidateResult(tool, route));
    }

    [Theory]
    [InlineData("harness-planning", "meal_ideas")]
    [InlineData("harness-assistant", "todos_unregistered")]
    [InlineData("harness-skills", "todos_get_all")]
    public void OtherHarnessToolsRemainRejected(string route, string name) =>
        Assert.Throws<InvalidOperationException>(() => ScenarioToolProjection.ValidateResult(new() { Name = name, Result = "result" }, route));

    [Theory]
    [InlineData("list_available_models", """{"models":[],"totalCount":0}""")]
    [InlineData("get_model_details", """{"found":false,"id":7}""")]
    public void ValidEmptyAndMissingCatalogueResultsRemainTyped(string name, string result) =>
        ScenarioToolProjection.ValidateResult(new() { Name = name, Result = result });
}

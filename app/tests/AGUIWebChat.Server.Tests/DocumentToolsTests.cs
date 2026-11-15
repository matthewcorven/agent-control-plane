using System.Text.Json;
using System.Reflection;
using AGUIWebChat.Contracts.Agents;
using AGUIWebChat.Server.Agents;
using AGUIWebChat.Server.Agents.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public class DocumentToolsTests
{
    public static IEnumerable<object[]> InvalidArguments()
    {
        foreach (bool predictive in new[] { false, true })
        {
            foreach (string argument in predictive
                ? new[] { "title", "text", "originalTitle", "originalText" }
                : new[] { "title", "text" })
            {
                yield return [predictive, argument, "missing"];
                yield return [predictive, argument, "null"];
                if (argument != "text") yield return [predictive, argument, "empty"];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task InvalidSdkArgumentsDoNotMutateDocument(bool predictive, string argument, string invalid)
    {
        var state = State("Original", "Old text");
        var arguments = new AIFunctionArguments
        {
            ["title"] = "Revised", ["text"] = "New text"
        };
        if (predictive)
        {
            arguments["originalTitle"] = "Original";
            arguments["originalText"] = "Old text";
        }
        if (invalid == "missing") arguments.Remove(argument);
        else arguments[argument] = invalid == "null" ? null : "";

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await Function(state, predictive).InvokeAsync(arguments, TestContext.Current.CancellationToken));

        var document = state.Read("document", AgentServerSerializerContext.Default.DocumentState);
        Assert.NotNull(document);
        Assert.Equal("Original", document.Title);
        Assert.Equal("Old text", document.Text);
        Assert.Equal("keep", state.Read("custom", AgentServerSerializerContext.Default.String));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, " ")]
    [InlineData(false, "New text")]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    [InlineData(true, "New text")]
    public async Task SdkTextRemainsAnExactString(bool predictive, string text)
    {
        var state = State("", "");
        var arguments = new AIFunctionArguments { ["title"] = "Revised", ["text"] = text };
        if (predictive)
        {
            arguments["originalTitle"] = "";
            arguments["originalText"] = "";
        }

        var result = Assert.IsType<JsonElement>(
            await Function(state, predictive).InvokeAsync(arguments, TestContext.Current.CancellationToken));

        Assert.Equal(text, state.Read("document", AgentServerSerializerContext.Default.DocumentState)!.Text);
        if (predictive)
        {
            Assert.Equal(JsonValueKind.Array, result.ValueKind);
            Assert.Equal(2, result.GetArrayLength());
            Assert.Equal("test", result[0].GetProperty("op").GetString());
            Assert.Equal("/document", result[0].GetProperty("path").GetString());
            Assert.Equal("", result[0].GetProperty("value").GetProperty("title").GetString());
            Assert.Equal("", result[0].GetProperty("value").GetProperty("text").GetString());
            Assert.Equal("add", result[1].GetProperty("op").GetString());
            Assert.Equal("/document", result[1].GetProperty("path").GetString());
            Assert.Equal(text, result[1].GetProperty("value").GetProperty("text").GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Object, result.ValueKind);
            Assert.Equal(text, result.GetProperty("document").GetProperty("text").GetString());
            Assert.Equal("keep", result.GetProperty("custom").GetString());
        }
    }

    [Theory]
    [InlineData("title")]
    [InlineData("text")]
    public async Task PredictiveSdkRejectsEachStaleOriginalField(string field)
    {
        var state = State("Original", "Old text");
        var arguments = new AIFunctionArguments
        {
            ["title"] = "Revised", ["text"] = "New text",
            ["originalTitle"] = field == "title" ? "Stale" : "Original",
            ["originalText"] = field == "text" ? "Stale" : "Old text"
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Function(state, predictive: true).InvokeAsync(arguments, TestContext.Current.CancellationToken));

        var document = state.Read("document", AgentServerSerializerContext.Default.DocumentState)!;
        Assert.Equal("Original", document.Title);
        Assert.Equal("Old text", document.Text);
    }

    private static ScenarioState State(string title, string text) => new(
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["document"] = new DocumentState { Title = title, Text = text },
            ["custom"] = "keep"
        }, AgentServerSerializerContext.Default.IDictionaryStringObject));

    // HTTP tests cover real approval; unwrapping here lets us inspect the tool's state directly.
    private static AIFunction Function(ScenarioState state, bool predictive) => predictive
        ? Assert.IsAssignableFrom<AIFunction>(typeof(DelegatingAIFunction)
            .GetProperty("InnerFunction", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Assert.IsType<ApprovalRequiredAIFunction>(Assert.Single(DocumentTools.CreatePredictiveTools(state)))))
        : Assert.IsAssignableFrom<AIFunction>(Assert.Single(DocumentTools.CreateTools(state)));
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AGUIWebChat.Client.Services;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class ScenarioStatePatchTests
{
    [Theory]
    [InlineData("""{"a":1}""", """[{"op":"add","path":"/b","value":null}]""", """{"a":1,"b":null}""")]
    [InlineData("""{"a":1}""", """[{"op":"replace","path":"/a","value":2}]""", """{"a":2}""")]
    [InlineData("""{"a":1}""", """[{"op":"remove","path":"/a"}]""", """{}""")]
    [InlineData("""{"a":1}""", """[{"op":"test","path":"/a","value":1.0}]""", """{"a":1}""")]
    [InlineData("""{"a":null}""", """[{"op":"copy","from":"/a","path":"/b"}]""", """{"a":null,"b":null}""")]
    [InlineData("""{"a":{"x":1}}""", """[{"op":"copy","from":"/a","path":"/b"},{"op":"replace","path":"/b/x","value":2}]""", """{"a":{"x":1},"b":{"x":2}}""")]
    [InlineData("""{"a":1}""", """[{"op":"move","from":"/a","path":"/b"}]""", """{"b":1}""")]
    [InlineData("""{"a":1}""", """[{"op":"move","from":"/a","path":"/a"}]""", """{"a":1}""")]
    [InlineData("""{"a":[1,2,3]}""", """[{"op":"move","from":"/a/0","path":"/a/2"}]""", """{"a":[2,3,1]}""")]
    [InlineData("""{"a":[1]}""", """[{"op":"add","path":"/a/-","value":2}]""", """{"a":[1,2]}""")]
    [InlineData("""{"a":[1]}""", """[{"op":"add","path":"/a/0","value":2}]""", """{"a":[2,1]}""")]
    [InlineData("""{"a":1}""", """[{"op":"replace","path":"","value":{"b":2}}]""", """{"b":2}""")]
    [InlineData("""{"a/b":{"~":1},"":0}""", """[{"op":"replace","path":"/a~1b/~0","value":2},{"op":"remove","path":"/"}]""", """{"a/b":{"~":2}}""")]
    public void AppliesRfc6902WithoutMutatingInput(string source, string delta, string expected)
    {
        var original = JsonNode.Parse(source)!.AsObject();
        using var document = JsonDocument.Parse(delta);
        var result = ScenarioStatePatch.Apply(original, document.RootElement);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), result));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source), original));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[{"path":"/a","value":1}]""")]
    [InlineData("""[{"op":"replace","path":"/a"}]""")]
    [InlineData("""[{"op":"add","path":"/b"}]""")]
    [InlineData("""[{"op":"test","path":"/missing","value":null}]""")]
    [InlineData("""[{"op":"remove","path":"/a/~2"}]""")]
    [InlineData("""[{"op":"remove","path":"/array/01"}]""")]
    [InlineData("""[{"op":"remove","path":"/array/+0"}]""")]
    [InlineData("""[{"op":"remove","path":"/array/-1"}]""")]
    [InlineData("""[{"op":"replace","path":"/array/-","value":1}]""")]
    [InlineData("""[{"op":"add","path":"/array/2","value":1}]""")]
    [InlineData("""[{"op":"move","from":"/a","path":"/a/child"}]""")]
    [InlineData("""[{"op":"copy","from":"/missing","path":"/b"}]""")]
    [InlineData("""[{"op":"replace","path":"","value":null}]""")]
    [InlineData("""[{"op":"replace","path":"/a","value":2},{"op":"test","path":"/a","value":1}]""")]
    public void InvalidPatchFailsAtomically(string delta)
    {
        const string source = """{"a":1,"array":[0]}""";
        var original = JsonNode.Parse(source)!.AsObject();
        using var document = JsonDocument.Parse(delta);
        Assert.ThrowsAny<Exception>(() => ScenarioStatePatch.Apply(original, document.RootElement));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source), original));
    }

    [Fact]
    public void DocumentAndUnknownInferenceStateSurviveTypedValidation()
    {
        var original = JsonNode.Parse("""{"document":{"title":"Original","text":" "},"temperature":0.2,"future":{"enabled":true}}""")!.AsObject();
        using var document = JsonDocument.Parse("""[{"op":"replace","path":"/document/text","value":""}]""");
        var result = ScenarioStatePatch.Apply(original, document.RootElement);
        ScenarioSession.ValidateState(result);
        Assert.Equal("", result["document"]!["text"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(original["future"], result["future"]));
        Assert.Equal(0.2, result["temperature"]!.GetValue<double>());
    }

    [Fact]
    public void CopyGrowthIsBoundedWithoutMutatingOriginal()
    {
        var original = JsonNode.Parse("""{"data":"bounded","children":[]}""")!.AsObject();
        var delta = JsonSerializer.Serialize(Enumerable.Repeat(new { op = "copy", from = "", path = "/children/-" }, 32));
        using var document = JsonDocument.Parse(delta);
        Assert.Throws<InvalidOperationException>(() => ScenarioStatePatch.Apply(original, document.RootElement));
        Assert.Empty(original["children"]!.AsArray());
    }
}

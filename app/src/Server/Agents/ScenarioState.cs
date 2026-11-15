using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AGUIWebChat.Contracts.Agents;

namespace AGUIWebChat.Server.Agents;

internal sealed class ScenarioState
{
    private readonly JsonObject _root;

    public ScenarioState(JsonElement? state)
    {
        if (state is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw new ArgumentException("Scenario state must be an object.");
        }

        _root = state is { ValueKind: JsonValueKind.Object } value
            ? JsonNode.Parse(value.GetRawText())!.AsObject()
            : new JsonObject();
    }

    public T? Read<T>(string property, JsonTypeInfo<T> typeInfo) =>
        _root[property] is { } value ? value.Deserialize(typeInfo) : default;

    public JsonElement Snapshot<T>(string property, T value, JsonTypeInfo<T> typeInfo)
    {
        _root[property] = JsonSerializer.SerializeToNode(value, typeInfo);
        return JsonSerializer.SerializeToElement(_root, AgentServerSerializerContext.Default.JsonObject);
    }

    public JsonPatchOperation[] Replace<T>(string property, T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.SerializeToElement(value, typeInfo);
        _root[property] = JsonNode.Parse(json.GetRawText());
        return [new JsonPatchOperation { Op = "add", Path = "/" + property, Value = json }];
    }

    public static JsonElement Delta(IReadOnlyList<JsonPatchOperation> changes) =>
        JsonSerializer.SerializeToElement(changes, AgentServerSerializerContext.Default.IReadOnlyListJsonPatchOperation);
}

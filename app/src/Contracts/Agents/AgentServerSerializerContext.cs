using System.Text.Json.Serialization;
using AGUIWebChat.Contracts.AI.Models;
using AGUIWebChat.Contracts.AI.Providers;

namespace AGUIWebChat.Contracts.Agents
{
    /// <summary>
    /// Source-generated serializer context for every payload the scenario agents
    /// exchange with the client. Keeping the tool DTOs here means the server can
    /// run with reflection-based <c>System.Text.Json</c> disabled.
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ModelCatalogResult))]
    [JsonSerializable(typeof(ModelCatalogEntry))]
    [JsonSerializable(typeof(ModelDetailsResult))]
    [JsonSerializable(typeof(ModelSwitchResult))]
    [JsonSerializable(typeof(PlanResult))]
    [JsonSerializable(typeof(PlanStep))]
    [JsonSerializable(typeof(PlanStepUpdate))]
    [JsonSerializable(typeof(DocumentState))]
    [JsonSerializable(typeof(JsonPatchOperation[]))]
    [JsonSerializable(typeof(IReadOnlyList<JsonPatchOperation>))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(System.Text.Json.JsonElement))]
    [JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
    [JsonSerializable(typeof(IDictionary<string, object?>))]
    [JsonSerializable(typeof(AIModelEditModel))]
    [JsonSerializable(typeof(IReadOnlyList<AIModelEditModel>))]
    [JsonSerializable(typeof(AIProviderListItem))]
    [JsonSerializable(typeof(IReadOnlyList<AIProviderListItem>))]
    public sealed partial class AgentServerSerializerContext : JsonSerializerContext
    {
    }
}

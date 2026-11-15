using AGUIWebChat.Client.Models;
using AGUIWebChat.Contracts.Agents;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AGUIWebChat.Client.Formatting;

public static class ScenarioToolProjection
{
    public static T? Result<T>(ScenarioToolModel tool, JsonTypeInfo<T> typeInfo) =>
        tool.Result is null || tool.Status == "Failed" || tool.Result.StartsWith("Tool call invocation rejected.", StringComparison.Ordinal)
            ? default : JsonSerializer.Deserialize(tool.Result, typeInfo)
                ?? throw new InvalidOperationException("The tool result is missing its typed payload.");

    public static int ModelId(ScenarioToolModel tool)
    {
        using var arguments = JsonDocument.Parse(tool.Arguments);
        ValidateArguments(tool, arguments.RootElement);
        return arguments.RootElement.GetProperty("modelId").GetInt32();
    }

    public static DocumentState ProposedDocument(ScenarioToolModel tool)
    {
        using var arguments = JsonDocument.Parse(tool.Arguments);
        ValidateArguments(tool, arguments.RootElement);
        return new()
        {
            Title = arguments.RootElement.GetProperty("title").GetString()
                ?? throw new InvalidOperationException("A proposed document needs a title."),
            Text = arguments.RootElement.GetProperty("text").GetString()
                ?? throw new InvalidOperationException("A proposed document needs text.")
        };
    }

    public static void ValidateArguments(ScenarioToolModel tool)
    {
        using var arguments = JsonDocument.Parse(tool.Arguments);
        ValidateArguments(tool, arguments.RootElement);
    }

    private static void ValidateArguments(ScenarioToolModel tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new JsonException("Scenario tool arguments must be an object.");
        if (tool.InterruptId is not null && tool.Name is not ("switch_active_model" or "propose_document_edit"))
            throw new JsonException("The scenario tool does not support approval.");
        switch (tool.Name)
        {
            case "switch_active_model":
                if (!arguments.TryGetProperty("modelId", out var modelId) ||
                    modelId.ValueKind != JsonValueKind.Number || !modelId.TryGetInt32(out int id) || id <= 0)
                    throw new JsonException("The model switch needs a positive Int32 modelId.");
                break;
            case "propose_document_edit":
                RequireString(arguments, "title", nonEmpty: true);
                RequireString(arguments, "text");
                RequireString(arguments, "originalTitle");
                RequireString(arguments, "originalText");
                break;
        }
    }

    private static void RequireString(JsonElement arguments, string name, bool nonEmpty = false)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            (nonEmpty && string.IsNullOrWhiteSpace(value.GetString())))
            throw new JsonException($"The proposed document needs a {(nonEmpty ? "non-empty " : "")}string {name}.");
    }

    public static void ValidateResult(ScenarioToolModel tool, string? route = null)
    {
        if (tool.Status == "Failed" || tool.Result is null ||
            tool.Result.StartsWith("Tool call invocation rejected.", StringComparison.Ordinal)) return;
        if ((route == "harness-skills" && tool.Name == "load_skill") ||
            (route == "harness-memory" && tool.Name is "file_memory_read" or "file_memory_write" or "file_memory_delete" or "file_memory_list") ||
            (route is "harness-planning" or "harness-assistant" && tool.Name is "todos_add" or "todos_complete" or "todos_remove" or "todos_get_remaining" or "todos_get_all" or "mode_set" or "mode_get") ||
            (route == "harness-assistant" && tool.Name == "meal_ideas"))
        {
            if (tool.Result.Length > 131072) throw new InvalidOperationException("The Harness tool result is too large.");
            return;
        }
        switch (tool.Name)
        {
            case "list_available_models":
                using (var doc = JsonDocument.Parse(tool.Result))
                {
                    if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array ||
                        !doc.RootElement.TryGetProperty("totalCount", out var total) || !total.TryGetInt32(out int count) ||
                        count != models.GetArrayLength())
                        throw new InvalidOperationException("The catalogue tool returned an invalid result.");
                }
                _ = Result(tool, AgentServerSerializerContext.Default.ModelCatalogResult);
                break;
            case "get_model_details":
                using (var doc = JsonDocument.Parse(tool.Result))
                    if (!doc.RootElement.TryGetProperty("found", out var found) || found.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidOperationException("The model details tool returned an invalid result.");
                _ = Result(tool, AgentServerSerializerContext.Default.ModelDetailsResult);
                break;
            case "switch_active_model":
                var switched = Result(tool, AgentServerSerializerContext.Default.ModelSwitchResult)!;
                if (!switched.Switched || switched.ModelId <= 0 || switched.Changes is null ||
                    !switched.Changes.Any(change => change.Op == "add" && change.Path == "/modelId" &&
                        change.Value.TryGetInt32(out int id) && id == switched.ModelId))
                    throw new InvalidOperationException("The model switch tool did not confirm the requested change.");
                break;
            case "update_document":
            case "create_plan":
                using (var doc = JsonDocument.Parse(tool.Result))
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException("The state tool returned an invalid snapshot.");
                break;
            case "propose_document_edit":
            case "update_plan_step":
                using (var doc = JsonDocument.Parse(tool.Result))
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("The state tool returned an invalid patch.");
                break;
            default: throw new InvalidOperationException("The server returned an unsupported scenario tool.");
        }
    }
}

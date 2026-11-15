using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AGUIWebChat.Client.Models;

public sealed class ScenarioTranscriptItem
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsUser { get; set; }
}

public sealed class ScenarioToolModel
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string? Result { get; set; }
    public string? InterruptId { get; set; }
    public bool? Approved { get; set; }
    public bool? Decision { get; set; }
    public bool StateApplied { get; set; }
    public string Status { get; set; } = "Running";
}

public sealed class ScenarioReasoningState
{
    public string Text { get; set; } = "";
    public int ChunkCount { get; set; }
    public double DurationMs { get; set; }
}

public sealed record ScenarioDecision(ScenarioToolModel Tool, bool Approved);

public sealed class ScenarioCheckpoint
{
    public int Version { get; set; } = 1;
    public string Route { get; set; } = "";
    public string ThreadId { get; set; } = Guid.NewGuid().ToString("N");
    public JsonObject State { get; set; } = new();
    public JsonArray History { get; set; } = new();
    public List<ScenarioTranscriptItem> Transcript { get; set; } = [];
    public List<ScenarioToolModel> Tools { get; set; } = [];
    public ScenarioReasoningState? Reasoning { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScenarioCheckpoint))]
public partial class ScenarioJsonContext : JsonSerializerContext;

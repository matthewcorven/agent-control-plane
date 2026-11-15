namespace AGUIWebChat.Contracts.Agents
{
    /// <summary>
    /// Result of the <c>list_available_models</c> scenario tool.
    /// </summary>
    public sealed class ModelCatalogResult
    {
        public IReadOnlyList<ModelCatalogEntry> Models { get; set; } = [];

        public int TotalCount { get; set; }
    }

    /// <summary>
    /// A single model as exposed to an agent tool. Deliberately narrower than
    /// <see cref="AI.Models.AIModelEditModel"/> so the tool payload stays small.
    /// </summary>
    public sealed class ModelCatalogEntry
    {
        public int Id { get; set; }

        public string ModelId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string ProviderName { get; set; } = string.Empty;

        public bool SupportsTools { get; set; }

        public bool SupportsVision { get; set; }

        public int? ContextWindow { get; set; }
    }

    /// <summary>
    /// Result of the <c>get_model_details</c> scenario tool.
    /// </summary>
    public sealed class ModelDetailsResult
    {
        public bool Found { get; set; }

        public int Id { get; set; }

        public string ModelId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string ProviderName { get; set; } = string.Empty;

        public string ThinkingMode { get; set; } = string.Empty;

        public bool SupportsTools { get; set; }

        public bool SupportsVision { get; set; }

        public bool SupportsStreaming { get; set; }

        public int? ContextWindow { get; set; }

        public int? MaxOutputTokens { get; set; }

        public IReadOnlyList<string> ReasoningEfforts { get; set; } = [];
    }

    /// <summary>
    /// Result of the <c>switch_active_model</c> scenario tool. This tool is
    /// consequential, so it is wrapped in an approval requirement.
    /// </summary>
    public sealed class ModelSwitchResult
    {
        public bool Switched { get; set; }

        public int ModelId { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public IReadOnlyList<JsonPatchOperation> Changes { get; set; } = [];
    }

    /// <summary>
    /// One step of a plan produced by the generative-UI scenario. The result of
    /// <c>create_plan</c> is mapped to an AG-UI state snapshot.
    /// </summary>
    public sealed class PlanStep
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Status { get; set; } = "pending";
    }

    /// <summary>
    /// Result of the <c>create_plan</c> scenario tool.
    /// </summary>
    public sealed class PlanResult
    {
        public string Title { get; set; } = string.Empty;

        public IReadOnlyList<PlanStep> Steps { get; set; } = [];
    }

    /// <summary>
    /// Result of the <c>update_plan_step</c> scenario tool. Mapped to an AG-UI
    /// state delta.
    /// </summary>
    public sealed class PlanStepUpdate
    {
        public string Id { get; set; } = string.Empty;

        public string Status { get; set; } = "pending";
    }

    public sealed class DocumentState
    {
        public string Title { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;
    }

    public sealed class JsonPatchOperation
    {
        public string Op { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public System.Text.Json.JsonElement Value { get; set; }
    }
}

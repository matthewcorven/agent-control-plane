using AGUIWebChat.Client.Services;
using Microsoft.AspNetCore.Components.AI;

namespace AGUIWebChat.Client.Models
{
    public enum ChatRole
    {
        User,
        Assistant,
        System
    }

    public sealed class ChatMessageModel
    {
        public required string Id { get; init; }

        public ChatRole Role { get; init; }

        public string Content { get; set; } = string.Empty;

        public string? StatusMessage { get; set; }

        public string? Reasoning { get; set; }

        public string? RunId { get; set; }

        public double? ReasoningDurationMs { get; set; }

        public int? ReasoningChunkCount { get; set; }

        public int? ReasoningCharacterCount { get; set; }

        public bool IsStreaming { get; set; }

        /// <summary>
        /// Structured nodes parsed from <see cref="Content"/>. When present the
        /// transcript renders these instead of re-parsing the Markdown.
        /// </summary>
        public IReadOnlyList<RichTextNode>? Nodes { get; set; }

        public LlmTelemetryDto? MonitoringTelemetry { get; set; }

        internal static ChatMessageModel? FindForTelemetry(
            IReadOnlyList<ChatMessageModel> messages, ChatMessageModel? current,
            string? preparedRunId, string telemetryRunId)
        {
            if (string.IsNullOrWhiteSpace(telemetryRunId)) return null;
            var message = messages.FirstOrDefault(item => item.RunId == telemetryRunId);
            if (message is not null) return message;
            // SignalR can finish before the browser receives RUN_STARTED.
            if (current is { IsStreaming: true, RunId: null } && messages.Contains(current)
                && preparedRunId == telemetryRunId)
            {
                current.RunId = preparedRunId;
                return current;
            }
            return null;
        }
    }
}

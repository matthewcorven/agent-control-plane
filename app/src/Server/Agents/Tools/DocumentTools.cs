using AGUIWebChat.Contracts.Agents;
using Microsoft.Extensions.AI;
using Serilog;
using System.Text.Json;

namespace AGUIWebChat.Server.Agents.Tools;

internal static class DocumentTools
{
    public const string UpdateDocumentName = "update_document";
    public const string ProposeDocumentEditName = "propose_document_edit";

    public static IList<AITool> CreateTools(ScenarioState state) =>
    [
        AIFunctionFactory.Create((string title, string text) =>
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                Log.Warning("Rejected document update with empty title.");
                throw new ArgumentException("Document title must not be empty.");
            }
            if (text is null)
            {
                Log.Warning("Rejected document update with null text.");
                throw new ArgumentException("Document text must not be null.", nameof(text));
            }

            return state.Snapshot("document", new DocumentState { Title = title, Text = text },
                AgentServerSerializerContext.Default.DocumentState);
        }, new AIFunctionFactoryOptions
        {
            Name = UpdateDocumentName,
            Description = "Replace the shared document title and text. Preserve the user's intent.",
            SerializerOptions = AgentServerSerializerContext.Default.Options
        })
    ];

    public static IList<AITool> CreatePredictiveTools(ScenarioState state) =>
    [
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
            (string title, string text, string originalTitle, string originalText) =>
            {
                if (text is null || originalTitle is null || originalText is null)
                {
                    Log.Warning("Rejected predictive edit with null text, original title, or original text.");
                    throw new ArgumentException("Document text, originalTitle, and originalText must not be null.");
                }
                var current = state.Read("document", AgentServerSerializerContext.Default.DocumentState);
                if (string.IsNullOrWhiteSpace(title)
                    || (current?.Title ?? "") != originalTitle || (current?.Text ?? "") != originalText)
                {
                    Log.Warning("Rejected predictive edit: empty title or document changed since proposal.");
                    throw new ArgumentException("The document changed since this proposal, or the title is empty. Request a new proposal.");
                }

                var changes = state.Replace("document", new DocumentState { Title = title, Text = text },
                    AgentServerSerializerContext.Default.DocumentState).ToList();
                if (current is not null)
                {
                    changes.Insert(0, new JsonPatchOperation
                    {
                        Op = "test", Path = "/document",
                        Value = JsonSerializer.SerializeToElement(current, AgentServerSerializerContext.Default.DocumentState)
                    });
                }

                return ScenarioState.Delta(changes);
            },
            new AIFunctionFactoryOptions
            {
                Name = ProposeDocumentEditName,
                Description = "Propose replacement document text for human approval. Include the exact current title and text as originalTitle and originalText. No change is applied until accepted.",
                SerializerOptions = AgentServerSerializerContext.Default.Options
            }))
    ];
}

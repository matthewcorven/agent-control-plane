using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AGUIWebChat.Server.Agents;

internal static class HarnessAssistant
{
    internal static AIAgent Create(IChatClient client)
    {
        AIAgent helper = client.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "MealHelper",
            Description = "Suggests practical meal ideas.",
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableToolAutoApproval = true,
            DisableOpenTelemetry = true,
            ChatOptions = new ChatOptions { Instructions = "Suggest concise meal ideas. You have no file access." }
        });
        return client.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "HouseholdAssistant",
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableToolAutoApproval = true,
            DisableOpenTelemetry = true,
            MaximumIterationsPerRequest = 4,
            ChatOptions = new ChatOptions
            {
                Instructions = "Help with household tasks. Use planning and todos for multi-step work. Use meal_ideas for meal suggestions when useful. You have no file access. Never claim a delegated task has completed without its result.",
                Tools = [AIFunctionFactory.Create(async (string request, CancellationToken cancellationToken) =>
                    (await helper.RunAsync(request, cancellationToken: cancellationToken)).Text,
                    "meal_ideas", "Ask the meal helper for practical meal ideas and wait for its answer.")]
            }
        });
    }
}

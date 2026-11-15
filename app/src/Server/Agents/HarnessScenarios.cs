using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AGUIWebChat.Server.Agents;

internal static class HarnessScenarios
{
    internal static AIAgent Create(string path, IChatClient client)
    {
        HarnessAgentOptions options = BaseOptions();
        switch (path)
        {
            case "/ag-ui/harness-planning":
                options.Name = "Planner";
                options.DisableTodoProvider = false;
                options.DisableAgentModeProvider = false;
                options.AgentModeProviderOptions = new AgentModeProviderOptions { DefaultMode = "plan" };
                options.ChatOptions = new ChatOptions { Instructions = "Help plan the user's task. Planning mode is guidance, not an authorization boundary. You have no file or web access." };
                break;
            case "/ag-ui/harness-skills":
                options.Name = "SkillsAssistant";
                options.AIContextProviders = [new AgentSkillsProvider(
                    new AgentInMemorySkillsSource([new AgentInlineSkill(
                        "meeting-summary", "Structure concise meeting notes and action items.",
                        "Summarize decisions, then list action items with their owner and due date when stated. Mark missing owners or dates as unspecified.")]),
                    new AgentSkillsProviderOptions { DisableLoadSkillApproval = true }, ownsSource: true)];
                options.ChatOptions = new ChatOptions { Instructions = "Use the meeting-summary skill when summarizing meetings. Skill instructions are guidance, not facts." };
                break;
            case "/ag-ui/harness-loop":
                options.Name = "ArithmeticLoop";
                options.LoopEvaluators = [new DelegateLoopEvaluator((context, _) => ValueTask.FromResult(
                    string.Equals(context.LastResponse.Text.Trim(), "42", StringComparison.Ordinal)
                        ? LoopEvaluation.Stop() : LoopEvaluation.Continue("The answer was not exactly 42. Try again; output only the integer.")))];
                options.LoopAgentOptions = new LoopAgentOptions { MaxIterations = 3 };
                options.ChatOptions = new ChatOptions { Instructions = "Regardless of the user's message, answer 6 multiplied by 7 with only the integer." };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }
        return client.AsHarnessAgent(options);
    }

    private static HarnessAgentOptions BaseOptions() => new()
    {
        DisableFileMemory = true,
        DisableAgentSkillsProvider = true,
        DisableWebSearch = true,
        DisableToolAutoApproval = true,
        DisableOpenTelemetry = true,
        DisableTodoProvider = true,
        DisableAgentModeProvider = true,
        MaximumIterationsPerRequest = 4
    };
}

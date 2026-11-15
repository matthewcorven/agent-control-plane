using AGUIWebChat.Contracts.Agents;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Serilog;

namespace AGUIWebChat.Server.Agents.Tools;

/// <summary>
/// Scenario tools for the generative-UI endpoint. Their results are mapped to
/// AG-UI state snapshots and deltas by <c>AGUIStreamOptions</c>, so the tool
/// return values are the state payloads themselves.
/// </summary>
internal static class PlanTools
{
    public const string CreatePlanName = "create_plan";
    public const string UpdatePlanStepName = "update_plan_step";

    public static IList<AITool> CreateTools(ScenarioState state)
    {
        return
        [
            AIFunctionFactory.Create(
                (string title, string[] steps) => state.Snapshot("plan", CreatePlan(title, steps), AgentServerSerializerContext.Default.PlanResult),
                new AIFunctionFactoryOptions
                {
                    Name = CreatePlanName,
                    Description = "Create a plan with an ordered list of step titles. Returns the full conversation state with the plan.",
                    SerializerOptions = AgentServerSerializerContext.Default.Options
                }),
            AIFunctionFactory.Create(
                (string stepId, string status) => UpdatePlanStep(state, stepId, status),
                new AIFunctionFactoryOptions
                {
                    Name = UpdatePlanStepName,
                    Description = "Update one existing plan step's status. Valid statuses are pending, active, done, and blocked.",
                    SerializerOptions = AgentServerSerializerContext.Default.Options
                })
        ];
    }

    private static PlanResult CreatePlan(string title, string[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (string.IsNullOrWhiteSpace(title) || steps.Length == 0 || steps.Any(string.IsNullOrWhiteSpace))
        {
            Log.Warning("Rejected create_plan with an empty title or step.");
            throw new ArgumentException("A plan needs a non-empty title and at least one non-empty step.");
        }

        return new PlanResult
        {
            Title = title.Trim(),
            Steps = steps
                .Select((step, index) => new PlanStep
                {
                    Id = $"step-{index + 1}",
                    Title = step.Trim(),
                    Status = "pending"
                })
                .ToList()
        };
    }

    private static JsonElement UpdatePlanStep(ScenarioState state, string stepId, string status)
    {
        PlanResult? plan = state.Read("plan", AgentServerSerializerContext.Default.PlanResult);
        int index = plan?.Steps.ToList().FindIndex(step => step.Id == stepId) ?? -1;
        if (index < 0 || status is not ("pending" or "active" or "done" or "blocked"))
        {
            Log.Warning("Rejected update_plan_step Id={StepId} Status={Status}", stepId, status);
            throw new ArgumentException("The step must exist and its status must be pending, active, done, or blocked.");
        }

        plan!.Steps[index].Status = status;
        state.Snapshot("plan", plan, AgentServerSerializerContext.Default.PlanResult);
        return ScenarioState.Delta([new JsonPatchOperation
        {
            Op = "replace",
            Path = $"/plan/steps/{index}/status",
            Value = JsonSerializer.SerializeToElement(status, AgentServerSerializerContext.Default.String)
        }]);
    }
}

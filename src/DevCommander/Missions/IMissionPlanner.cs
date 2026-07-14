using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using NovaCore.Agents;

namespace DevCommander.Missions;

public interface IMissionPlanner
{
    Task<MissionPlan> PlanAsync(MissionSpecDocument spec, CancellationToken ct);
}

public sealed class MissionPlanner(
    [FromKeyedServices("planner")] IAgentFactory plannerFactory) : IMissionPlanner
{
    public async Task<MissionPlan> PlanAsync(MissionSpecDocument spec, CancellationToken ct)
    {
        var prompt = $"""
            Decompose this immutable mission into a dependency-aware plan.
            Return only MissionPlan tasks with repository IDs exactly as listed, positive contiguous phases,
            and non-empty descriptions. Every listed repository must have at least one task.

            {spec.RawContent}
            """;

        var outcome = await plannerFactory.Create().RunStructuredAsync<MissionPlan>(AgentRunRequest.From(prompt), ct);
        return outcome switch
        {
            ExecutionOutcome<MissionPlan>.Completed { Value: not null } completed => completed.Value,
            ExecutionOutcome<MissionPlan>.Failed failed => throw new InvalidOperationException($"Mission planner failed: {failed}"),
            ExecutionOutcome<MissionPlan>.Exhausted exhausted => throw new InvalidOperationException($"Mission planner exhausted: {exhausted.Reason}"),
            ExecutionOutcome<MissionPlan>.Cancelled => throw new OperationCanceledException("Mission planner was cancelled.", ct),
            _ => throw new InvalidOperationException("Mission planner did not produce a plan.")
        };
    }
}

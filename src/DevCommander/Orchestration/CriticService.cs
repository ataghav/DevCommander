using Microsoft.Extensions.DependencyInjection;
using NovaCore.Agents;

namespace DevCommander.Orchestration;

public sealed record CriticVerdict(bool Approved, IReadOnlyList<string> BlockingFindings, string? Notes);

public interface ICriticService
{
    Task<CriticVerdict> ReviewAsync(string taskDescription, string diff, CancellationToken ct);
}

public sealed class CriticService(
    [FromKeyedServices("critic")] IAgentFactory criticFactory) : ICriticService
{
    public async Task<CriticVerdict> ReviewAsync(string taskDescription, string diff, CancellationToken ct)
    {
        var prompt = $"""
            Review only this current-task diff. Approve only when it fulfills the task without blocking defects.
            Task: {taskDescription}
            Diff:
            {diff}
            """;
        var outcome = await criticFactory.Create().RunStructuredAsync<CriticVerdict>(AgentRunRequest.From(prompt), ct);
        var verdict = outcome switch
        {
            ExecutionOutcome<CriticVerdict>.Completed { Value: not null } completed => completed.Value,
            ExecutionOutcome<CriticVerdict>.Failed failed => throw new InvalidOperationException($"Critic failed: {failed}"),
            ExecutionOutcome<CriticVerdict>.Exhausted exhausted => throw new InvalidOperationException($"Critic exhausted: {exhausted.Reason}"),
            ExecutionOutcome<CriticVerdict>.Cancelled => throw new OperationCanceledException("Critic was cancelled.", ct),
            _ => throw new InvalidOperationException("Critic did not return a verdict.")
        };
        if (verdict.BlockingFindings is null) throw new InvalidOperationException("Critic returned null blockingFindings.");
        return verdict;
    }
}

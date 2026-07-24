using DevCommander.Domain.Entities;
using DevCommander.Services;
using Microsoft.Extensions.DependencyInjection;
using NovaCore.Agents;

namespace DevCommander.HyperCare;

/// <summary>Triage agent contract per SRS §10.</summary>
public sealed record TriageResult(bool Confirmed, string Reason, string NormalizedSignature, string Summary);

/// <summary>Agent failure that still incurred LLM cost — callers reconcile it against their reservation.</summary>
public sealed class HyperCareAgentException(string message, decimal? accumulatedCostUsd, Exception? inner = null)
    : Exception(message, inner)
{
    public decimal? AccumulatedCostUsd { get; } = accumulatedCostUsd;
}

public sealed record TriageOutcome(TriageResult Result, decimal? ActualCostUsd);

public interface ITriageService
{
    /// <summary>One-shot false-positive judgment on a bounded candidate context. Throws after bounded retries.</summary>
    Task<TriageOutcome> TriageAsync(string serviceId, string boundedContext, CancellationToken ct);
}

public sealed class TriageService(
    [FromKeyedServices("triage")] IAgentFactory triageFactory,
    IAgentCostTracker costs) : ITriageService
{
    private const int MaxAttempts = 2;

    public async Task<TriageOutcome> TriageAsync(string serviceId, string boundedContext, CancellationToken ct)
    {
        var prompt = $"""
            Judge whether this filtered production signal from service '{serviceId}' is a real failure or a false positive.
            Signal excerpt (redacted, bounded):
            {boundedContext}
            """;
        Exception? last = null;
        decimal? accumulatedCost = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var outcome = await triageFactory.Create().RunStructuredAsync<TriageResult>(AgentRunRequest.From(prompt), ct);
            await costs.RecordFromOutcomeAsync("triage", outcome, missionId: null, ct);
            // Every attempt's cost counts against the session budget, not just the final one.
            var attemptCost = outcome switch
            {
                ExecutionOutcome<TriageResult>.Completed c => c.Report?.TotalCost,
                ExecutionOutcome<TriageResult>.Failed f => f.Report?.TotalCost,
                _ => null,
            };
            if (attemptCost is { } cost)
            {
                accumulatedCost = (accumulatedCost ?? 0m) + cost;
            }

            switch (outcome)
            {
                case ExecutionOutcome<TriageResult>.Completed { Value: { } value }
                    when !string.IsNullOrWhiteSpace(value.NormalizedSignature):
                    return new TriageOutcome(value with { NormalizedSignature = SanitizeSignature(value.NormalizedSignature) },
                        accumulatedCost);
                case ExecutionOutcome<TriageResult>.Cancelled:
                    throw new OperationCanceledException("Triage was cancelled.", ct);
                default:
                    last = new InvalidOperationException($"Triage attempt {attempt} failed: {outcome}");
                    break;
            }
        }

        throw new HyperCareAgentException(
            $"Triage failed after {MaxAttempts} attempts: {last!.Message}", accumulatedCost, last);
    }

    /// <summary>Enforces the signature contract (no whitespace, bounded) regardless of model behavior.</summary>
    private static string SanitizeSignature(string signature)
    {
        var collapsed = System.Text.RegularExpressions.Regex
            .Replace(signature.Trim(), @"\s+", "-")
            .ToLowerInvariant();
        return collapsed.Length <= 200 ? collapsed : collapsed[..200];
    }
}

/// <summary>Investigate agent contract per SRS §10 — replaces the planner for Hyper-Care tracks (BR-HC-014).</summary>
public sealed record InvestigateResult(
    string RootCause,
    IReadOnlyList<string> AffectedRepos,
    string TaskDescription,
    string Notes);

public sealed record InvestigateOutcome(InvestigateResult Result, decimal? ActualCostUsd);

public interface IInvestigateService
{
    Task<InvestigateOutcome> InvestigateAsync(HyperCareIssue issue, CancellationToken ct);
}

public sealed class InvestigateService(
    [FromKeyedServices("investigate")] IAgentFactory investigateFactory,
    IAgentCostTracker costs) : IInvestigateService
{
    public async Task<InvestigateOutcome> InvestigateAsync(HyperCareIssue issue, CancellationToken ct)
    {
        var prompt = $"""
            Root-cause this confirmed production issue and produce a coder-ready fix task.
            Service: {issue.ServiceId} (repository: {issue.RepoId})
            Summary: {issue.Summary}
            Severity: {issue.Severity} · Occurrences: {issue.OccurrenceCount} ({issue.FirstSeenAt:u} – {issue.LastSeenAt:u})
            Signature: {issue.Signature}
            Evidence (redacted samples):
            {issue.AttributesJson}
            """;
        var outcome = await investigateFactory.Create()
            .RunStructuredAsync<InvestigateResult>(AgentRunRequest.From(prompt), ct);
        await costs.RecordFromOutcomeAsync("investigate", outcome, issue.MissionId, ct);
        var actualCost = outcome switch
        {
            ExecutionOutcome<InvestigateResult>.Completed c => c.Report?.TotalCost,
            ExecutionOutcome<InvestigateResult>.Failed f => f.Report?.TotalCost,
            _ => null,
        };
        return outcome switch
        {
            ExecutionOutcome<InvestigateResult>.Completed { Value: { } value }
                when !string.IsNullOrWhiteSpace(value.TaskDescription) =>
                new InvestigateOutcome(value, actualCost),
            ExecutionOutcome<InvestigateResult>.Cancelled =>
                throw new OperationCanceledException("Investigate was cancelled.", ct),
            _ => throw new HyperCareAgentException($"Investigate failed: {outcome}", actualCost),
        };
    }
}

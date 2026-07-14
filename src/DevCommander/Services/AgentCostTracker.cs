using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using NovaCore.Agents;

namespace DevCommander.Services;

public interface IAgentCostTracker
{
    Task RecordAsync(string agentRole, ExecutionReport report, Guid? missionId, CancellationToken ct);
    Task RecordFromOutcomeAsync<T>(string agentRole, ExecutionOutcome<T> outcome, Guid? missionId, CancellationToken ct);
    Task RecordCoderAsync(RuntimeKind runtime, Guid missionId, decimal costUsd, bool isEstimated, CancellationToken ct);
    Task<CostLedgerReport> GetReportAsync(CancellationToken ct);
}

public sealed record AgentCostSummary(
    string AgentRole,
    int Runs,
    decimal TotalCostUsd,
    decimal LlmCostUsd,
    long InputTokens,
    long OutputTokens,
    bool IsEstimated);

public sealed record CostLedgerReport(
    IReadOnlyList<AgentCostSummary> Lines,
    decimal HostLlmExactUsd,
    decimal CodingBestEffortUsd,
    decimal GrandTotalUsd);

public sealed class AgentCostTracker(
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider time,
    ILogger<AgentCostTracker> logger) : IAgentCostTracker
{
    public static string CoderRole(RuntimeKind runtime) => $"coder:{runtime}";

    public Task RecordFromOutcomeAsync<T>(
        string agentRole,
        ExecutionOutcome<T> outcome,
        Guid? missionId,
        CancellationToken ct)
    {
        var report = outcome switch
        {
            ExecutionOutcome<T>.Completed completed => completed.Report,
            ExecutionOutcome<T>.Failed failed => failed.Report,
            _ => null
        };
        return report is null ? Task.CompletedTask : RecordAsync(agentRole, report, missionId, ct);
    }

    public async Task RecordAsync(string agentRole, ExecutionReport report, Guid? missionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AgentCostEntries.Add(new AgentCostEntry
        {
            Id = Guid.NewGuid(),
            AgentRole = agentRole,
            MissionId = missionId,
            TotalCostUsd = report.TotalCost,
            LlmCostUsd = report.LlmCost,
            InputTokens = report.Usage.InputTokens,
            OutputTokens = report.Usage.OutputTokens,
            TotalTokens = report.Usage.TotalTokens,
            IsEstimated = false,
            At = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Agent cost recorded role={Role} total={Total:F6} llm={Llm:F6} in={In} out={Out} mission={MissionId}",
            agentRole,
            report.TotalCost,
            report.LlmCost,
            report.Usage.InputTokens,
            report.Usage.OutputTokens,
            missionId);
    }

    public async Task RecordCoderAsync(
        RuntimeKind runtime,
        Guid missionId,
        decimal costUsd,
        bool isEstimated,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var role = CoderRole(runtime);
        db.AgentCostEntries.Add(new AgentCostEntry
        {
            Id = Guid.NewGuid(),
            AgentRole = role,
            MissionId = missionId,
            TotalCostUsd = costUsd,
            LlmCostUsd = costUsd,
            IsEstimated = isEstimated,
            At = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Coder cost recorded role={Role} total={Total:F6} estimated={Estimated} mission={MissionId}",
            role,
            costUsd,
            isEstimated,
            missionId);
    }

    public async Task<CostLedgerReport> GetReportAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentCostEntries.AsNoTracking().ToListAsync(ct);
        var lines = rows
            .GroupBy(x => x.AgentRole, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AgentCostSummary(
                g.Key,
                g.Count(),
                g.Sum(x => x.TotalCostUsd),
                g.Sum(x => x.LlmCostUsd),
                g.Sum(x => (long)x.InputTokens),
                g.Sum(x => (long)x.OutputTokens),
                g.Any(x => x.IsEstimated)))
            .ToList();

        var hostExact = lines
            .Where(l => !l.AgentRole.StartsWith("coder:", StringComparison.OrdinalIgnoreCase))
            .Sum(l => l.TotalCostUsd);
        var coding = lines
            .Where(l => l.AgentRole.StartsWith("coder:", StringComparison.OrdinalIgnoreCase))
            .Sum(l => l.TotalCostUsd);
        return new CostLedgerReport(lines, hostExact, coding, hostExact + coding);
    }
}

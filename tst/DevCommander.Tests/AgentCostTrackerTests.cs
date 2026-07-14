using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Services;
using DevCommander.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevCommander.Tests;

public sealed class AgentCostTrackerTests
{
    [Fact]
    public async Task GetReportAsync_BreaksDownHostLlmAndCodingCosts()
    {
        using var host = new TestHostFactory();
        var tracker = new AgentCostTracker(host.DbFactory, TimeProvider.System, NullLogger<AgentCostTracker>.Instance);

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.AgentCostEntries.AddRange(
                new AgentCostEntry
                {
                    Id = Guid.NewGuid(),
                    AgentRole = "commander",
                    TotalCostUsd = 0.001m,
                    LlmCostUsd = 0.001m,
                    InputTokens = 10,
                    OutputTokens = 5,
                    TotalTokens = 15,
                    IsEstimated = false,
                    At = DateTimeOffset.UtcNow,
                },
                new AgentCostEntry
                {
                    Id = Guid.NewGuid(),
                    AgentRole = "planner",
                    MissionId = Guid.NewGuid(),
                    TotalCostUsd = 0.002m,
                    LlmCostUsd = 0.002m,
                    InputTokens = 20,
                    OutputTokens = 8,
                    TotalTokens = 28,
                    IsEstimated = false,
                    At = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        await tracker.RecordCoderAsync(RuntimeKind.Claude, Guid.NewGuid(), 0.50m, isEstimated: true, CancellationToken.None);

        var report = await tracker.GetReportAsync(CancellationToken.None);
        Assert.Equal(0.003m, report.HostLlmExactUsd);
        Assert.Equal(0.50m, report.CodingBestEffortUsd);
        Assert.Equal(0.503m, report.GrandTotalUsd);
        Assert.Contains(report.Lines, l => l.AgentRole == "coder:Claude" && l.IsEstimated);
    }
}

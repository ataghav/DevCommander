using System.Diagnostics;
using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.Services;

public sealed class StartupReconciliationService(
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationOutbox outbox,
    TimeProvider time,
    ILogger<StartupReconciliationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var missions = await db.Missions
            .Where(x => x.Status != MissionStatus.Completed
                && x.Status != MissionStatus.Failed
                && x.Status != MissionStatus.Halted)
            .Include(x => x.Squads)
            .ToListAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var mission in missions)
        {
            foreach (var squad in mission.Squads)
            {
                await ReconcileProcessAsync(squad, cancellationToken);
            }

            var executing = await db.ApprovalRequests
                .Where(x => x.MissionId == mission.Id && x.State == ApprovalState.Executing)
                .ToListAsync(cancellationToken);
            foreach (var approval in executing)
            {
                approval.State = ApprovalState.Blocked;
            }

            if (executing.Count > 0 && mission.Status == MissionStatus.Running)
            {
                mission.Status = MissionStatus.Blocked;
                mission.Version++;
            }

            await outbox.EnqueueInTransactionAsync(
                db,
                mission.ChatId,
                $"recovery:{mission.Id:N}",
                NotificationSeverity.Warning,
                $"Recovery completed for mission '{mission.Slug}'. No completed tasks were reworked.",
                time.GetUtcNow());
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Startup reconciliation completed for {MissionCount} non-terminal missions", missions.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ReconcileProcessAsync(Squad squad, CancellationToken ct)
    {
        if (squad.LastPid is not { } pid || squad.ProcessStartedAt is not { } expectedStart)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (Math.Abs((actualStart - expectedStart).TotalSeconds) > 2)
            {
                squad.LastPid = null;
                squad.ProcessStartedAt = null;
                squad.Version++;
                return;
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct);
            squad.LastPid = null;
            squad.ProcessStartedAt = null;
            if (squad.Status is SquadStatus.Running or SquadStatus.Starting or SquadStatus.Stopping)
            {
                squad.Status = SquadStatus.Stopped;
            }

            squad.Version++;
        }
        catch (ArgumentException)
        {
            squad.LastPid = null;
            squad.ProcessStartedAt = null;
            squad.Version++;
        }
        catch (InvalidOperationException)
        {
            squad.LastPid = null;
            squad.ProcessStartedAt = null;
            squad.Version++;
        }
    }
}

using System.Text.Json;
using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Git;
using DevCommander.Services;
using Microsoft.EntityFrameworkCore;
using TaskStatus = DevCommander.Domain.TaskStatus;

namespace DevCommander.Orchestration;

public interface IMissionCoordinator
{
    Task CoordinateAsync(Guid missionId, CancellationToken ct);
}

public sealed class MissionCoordinator(
    IDbContextFactory<AppDbContext> dbFactory,
    IMissionRuntimeRegistry runtimes,
    IGitWorkspaceService git,
    INotificationOutbox outbox,
    TimeProvider time) : IMissionCoordinator
{
    public async Task CoordinateAsync(Guid missionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var mission = await db.Missions.SingleAsync(x => x.Id == missionId, ct);
            if (mission.Status is MissionStatus.Completed or MissionStatus.Failed or MissionStatus.Halted)
            {
                return;
            }

            if (mission.Deadline <= time.GetUtcNow())
            {
                mission.Status = MissionStatus.Halted;
                mission.ClosedAt = time.GetUtcNow();
                mission.Version++;
                await outbox.EnqueueInTransactionAsync(
                    db, mission.ChatId, $"wall-time:{mission.Id:N}", NotificationSeverity.Error,
                    "Mission halted: wall-time breach.", time.GetUtcNow());
                await db.SaveChangesAsync(ct);
                return;
            }

            var tasks = await db.Tasks.Where(x => x.MissionId == missionId).ToListAsync(ct);
            var squadsAll = await db.Squads.Where(x => x.MissionId == missionId).ToListAsync(ct);

            if (tasks.Any(x => x.Status == TaskStatus.RetriesExhausted))
            {
                mission.Status = MissionStatus.Failed;
                mission.ClosedAt = time.GetUtcNow();
                mission.Version++;
                await db.SaveChangesAsync(ct);
                return;
            }

            if (tasks.Count > 0 && tasks.All(x => x.Status == TaskStatus.Done))
            {
                foreach (var squad in squadsAll.Where(s => !s.Pushed))
                {
                    await git.PushBranchAsync(squad.RepoId, squad.WorktreePath, squad.Branch, ct);
                    squad.Pushed = true;
                    squad.Status = SquadStatus.Completed;
                    squad.Version++;
                    await git.RemoveWorktreeAsync(squad.RepoId, squad.WorktreePath, ct);
                }

                mission.Status = MissionStatus.Completed;
                mission.ClosedAt = time.GetUtcNow();
                mission.Version++;
                await outbox.EnqueueInTransactionAsync(
                    db, mission.ChatId, $"completed:{mission.Id:N}", NotificationSeverity.Info,
                    $"Mission '{mission.Slug}' completed.", time.GetUtcNow());
                await db.SaveChangesAsync(ct);
                return;
            }

            var unfinished = tasks.Where(x => x.Status != TaskStatus.Done).ToList();
            if (unfinished.Count == 0)
            {
                return;
            }

            var phase = unfinished.Min(x => x.Phase);
            var squadIds = unfinished.Where(x => x.Phase == phase).Select(x => x.SquadId).Distinct().ToList();
            var squads = squadsAll.Where(x => squadIds.Contains(x.Id)).ToList();

            if (squads.Any(x => x.Status == SquadStatus.Blocked)
                || unfinished.Any(x => x.Status == TaskStatus.Blocked && x.Phase == phase))
            {
                mission.Status = MissionStatus.Blocked;
                mission.Version++;
                await db.SaveChangesAsync(ct);
                return;
            }

            if (squads.Any(x => x.Status == SquadStatus.Stopped))
            {
                mission.Status = MissionStatus.Stopped;
                mission.Version++;
                await db.SaveChangesAsync(ct);
                return;
            }

            if (squads.Any(x => x.Status == SquadStatus.WaitingApproval))
            {
                // Squad paused for gated approval; keep mission non-terminal and wait.
                return;
            }

            // Persist phase summaries from completed lower phases for downstream context.
            var summaries = tasks
                .Where(x => x.Phase < phase && x.Status == TaskStatus.Done && !string.IsNullOrWhiteSpace(x.PhaseSummary))
                .Select(x => x.PhaseSummary!)
                .ToList();
            mission.PhaseSummariesJson = JsonSerializer.Serialize(summaries);
            mission.Status = MissionStatus.Running;
            mission.Version++;
            await db.SaveChangesAsync(ct);

            var eligible = squads.Where(x =>
                x.Status is SquadStatus.Pending or SquadStatus.Starting or SquadStatus.Running).ToList();
            await Task.WhenAll(eligible.Select(x => runtimes.StartSquadAsync(missionId, x.Id, phase, ct)));

            // Re-check after phase workers return; if still unfinished in this phase, stop for approval/block.
            await using var after = await dbFactory.CreateDbContextAsync(ct);
            var still = await after.Tasks
                .Where(x => x.MissionId == missionId && x.Phase == phase && x.Status != TaskStatus.Done)
                .ToListAsync(ct);
            if (still.Count > 0)
            {
                return;
            }
        }
    }
}

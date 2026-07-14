using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Services;
using Microsoft.EntityFrameworkCore;
using TaskStatus = DevCommander.Domain.TaskStatus;

namespace DevCommander.Orchestration;

public sealed record ApprovalKey(Guid MissionId, Guid SquadId, Guid TaskId, int Attempt, int CommandIndex, string CommandHash);

public interface IApprovalService
{
    Task<ApprovalRequest> RequireAsync(ApprovalKey key, string command, CancellationToken ct);
    Task<ApprovalRequest?> GetAsync(ApprovalKey key, CancellationToken ct);
    Task<ApprovalRequest?> FindAsync(Guid squadId, ApprovalState state, CancellationToken ct);
    Task<bool> ApproveAsync(Guid approvalId, long chatId, CancellationToken ct);
    Task<bool> BeginExecutionAsync(ApprovalKey key, CancellationToken ct);
    Task<bool> ConsumeAsync(ApprovalKey key, CancellationToken ct);
    Task BlockExecutingAsync(CancellationToken ct);
}

public sealed class ApprovalService(
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationOutbox outbox,
    TimeProvider time) : IApprovalService
{
    public async Task<ApprovalRequest> RequireAsync(ApprovalKey key, string command, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.ApprovalRequests.SingleOrDefaultAsync(x =>
            x.MissionId == key.MissionId && x.SquadId == key.SquadId && x.TaskId == key.TaskId &&
            x.Attempt == key.Attempt && x.CommandIndex == key.CommandIndex && x.CommandHash == key.CommandHash, ct);
        if (existing is not null)
        {
            return existing;
        }

        var squad = await db.Squads.SingleAsync(x => x.Id == key.SquadId, ct);
        var mission = await db.Missions.SingleAsync(x => x.Id == key.MissionId, ct);
        if (squad.Status is not (SquadStatus.Running or SquadStatus.WaitingApproval))
        {
            throw new InvalidOperationException("Squad cannot request approval in its current state.");
        }

        var approval = new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            MissionId = key.MissionId,
            SquadId = key.SquadId,
            TaskId = key.TaskId,
            Attempt = key.Attempt,
            CommandIndex = key.CommandIndex,
            CommandHash = key.CommandHash,
            Operation = command,
            RequestedAt = time.GetUtcNow(),
        };
        db.ApprovalRequests.Add(approval);
        squad.Status = SquadStatus.WaitingApproval;
        squad.Version++;
        db.SquadEvents.Add(new SquadEvent
        {
            Id = Guid.NewGuid(),
            SquadId = squad.Id,
            Kind = "ApprovalRequired",
            Payload = command,
            At = time.GetUtcNow(),
        });
        await outbox.EnqueueInTransactionAsync(
            db, mission.ChatId, $"approval:{approval.Id}", NotificationSeverity.Warning,
            $"Approval required for {squad.RepoId}: {command}", time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return approval;
    }

    public async Task<ApprovalRequest?> GetAsync(ApprovalKey key, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApprovalRequests.AsNoTracking().SingleOrDefaultAsync(x =>
            x.MissionId == key.MissionId && x.SquadId == key.SquadId && x.TaskId == key.TaskId &&
            x.Attempt == key.Attempt && x.CommandIndex == key.CommandIndex && x.CommandHash == key.CommandHash, ct);
    }

    public async Task<ApprovalRequest?> FindAsync(Guid squadId, ApprovalState state, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite cannot OrderBy DateTimeOffset; order in-memory.
        return (await db.ApprovalRequests.AsNoTracking()
                .Where(x => x.SquadId == squadId && x.State == state)
                .ToListAsync(ct))
            .OrderByDescending(x => x.RequestedAt)
            .FirstOrDefault();
    }

    public async Task<bool> ApproveAsync(Guid approvalId, long chatId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var approval = await db.ApprovalRequests.SingleOrDefaultAsync(
            x => x.Id == approvalId && x.State == ApprovalState.Pending, ct);
        if (approval is null)
        {
            return false;
        }

        approval.State = ApprovalState.Approved;
        approval.DecidedAt = time.GetUtcNow();
        approval.DecidedByChatId = chatId;
        var squad = await db.Squads.SingleAsync(x => x.Id == approval.SquadId, ct);
        if (squad.Status == SquadStatus.WaitingApproval)
        {
            squad.Status = SquadStatus.Starting;
            squad.Version++;
        }

        var task = await db.Tasks.SingleAsync(x => x.Id == approval.TaskId, ct);
        if (task.Status == TaskStatus.WaitingApproval)
        {
            task.Status = TaskStatus.Running;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<bool> BeginExecutionAsync(ApprovalKey key, CancellationToken ct) =>
        ChangeAsync(key, ApprovalState.Approved, ApprovalState.Executing, ct);

    public Task<bool> ConsumeAsync(ApprovalKey key, CancellationToken ct) =>
        ChangeAsync(key, ApprovalState.Executing, ApprovalState.Consumed, ct);

    public async Task BlockExecutingAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var executing = await db.ApprovalRequests.Where(x => x.State == ApprovalState.Executing).ToListAsync(ct);
        foreach (var approval in executing)
        {
            approval.State = ApprovalState.Blocked;
            var squad = await db.Squads.SingleAsync(x => x.Id == approval.SquadId, ct);
            squad.Status = SquadStatus.Blocked;
            squad.Version++;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> ChangeAsync(ApprovalKey key, ApprovalState from, ApprovalState to, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var approval = await db.ApprovalRequests.SingleOrDefaultAsync(x =>
            x.MissionId == key.MissionId && x.SquadId == key.SquadId && x.TaskId == key.TaskId &&
            x.Attempt == key.Attempt && x.CommandIndex == key.CommandIndex && x.CommandHash == key.CommandHash &&
            x.State == from, ct);
        if (approval is null)
        {
            return false;
        }

        approval.State = to;
        await db.SaveChangesAsync(ct);
        return true;
    }
}

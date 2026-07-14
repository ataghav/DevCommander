using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevCommander.Services;

public interface ICostAccountingService
{
    decimal GetEstimatedCharge(RuntimeKind kind);
    Task<bool> TryReserveAsync(Guid missionId, decimal estimatedCharge, CancellationToken ct);
    Task ReconcileAsync(Guid missionId, decimal? reportedCostUsd, decimal reservedEstimate, bool costIsEstimated, CancellationToken ct);
    Task<decimal> GetRemainingBudgetAsync(Guid missionId, CancellationToken ct);
}

public sealed class CostAccountingService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<DevCommanderOptions> options) : ICostAccountingService
{
    private readonly DevCommanderOptions _options = options.Value;

    public decimal GetEstimatedCharge(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Claude => _options.Runtimes.Claude.EstimatedChargeUsd,
        RuntimeKind.Codex => _options.Runtimes.Codex.EstimatedChargeUsd,
        RuntimeKind.Cursor => _options.Runtimes.Cursor.EstimatedChargeUsd,
        RuntimeKind.OpenCode => _options.Runtimes.OpenCode.EstimatedChargeUsd,
        _ => _options.Cost.DefaultEstimatedChargeUsd,
    };

    public async Task<bool> TryReserveAsync(Guid missionId, decimal estimatedCharge, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            var mission = await db.Missions.SingleAsync(m => m.Id == missionId, ct);
            var remaining = mission.BudgetUsd - mission.AccountedCostUsd;
            if (estimatedCharge > remaining)
            {
                return false;
            }

            // Count the reservation immediately so parallel workers cannot over-allocate
            // the same remaining budget. Reconcile replaces this estimate with actual cost.
            mission.AccountedCostUsd += estimatedCharge;
            mission.Version++;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct);
    }

    public async Task ReconcileAsync(
        Guid missionId,
        decimal? reportedCostUsd,
        decimal reservedEstimate,
        bool costIsEstimated,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            var mission = await db.Missions.SingleAsync(m => m.Id == missionId, ct);
            var charge = reportedCostUsd ?? reservedEstimate;
            mission.AccountedCostUsd += charge - reservedEstimate;
            mission.Version++;
            await db.SaveChangesAsync(ct);
        }, ct: ct);
    }

    public async Task<decimal> GetRemainingBudgetAsync(Guid missionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mission = await db.Missions.AsNoTracking().SingleAsync(m => m.Id == missionId, ct);
        return mission.BudgetUsd - mission.AccountedCostUsd;
    }
}

public interface INotificationOutbox
{
    Task EnqueueInTransactionAsync(
        AppDbContext db,
        long chatId,
        string logicalKey,
        NotificationSeverity severity,
        string body,
        DateTimeOffset now);
}

public sealed class NotificationOutbox : INotificationOutbox
{
    public async Task EnqueueInTransactionAsync(
        AppDbContext db,
        long chatId,
        string logicalKey,
        NotificationSeverity severity,
        string body,
        DateTimeOffset now)
    {
        if (db.Notifications.Local.Any(n => n.LogicalKey == logicalKey)
            || await db.Notifications.AnyAsync(n => n.LogicalKey == logicalKey))
        {
            return;
        }

        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            LogicalKey = logicalKey,
            Severity = severity,
            Body = body,
            State = NotificationState.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            At = now,
        });
    }
}

public interface IStateTransitionService
{
    Task<bool> TryUpdateMissionAsync(Guid missionId, MissionStatus expected, MissionStatus next, CancellationToken ct, Action<Mission>? mutate = null);
    Task<bool> TryUpdateSquadAsync(Guid squadId, SquadStatus expected, SquadStatus next, int expectedVersion, CancellationToken ct, Action<Squad>? mutate = null);
    Task AddEventAsync(AppDbContext db, Guid squadId, string kind, string payload, DateTimeOffset at);
}

public sealed class StateTransitionService(
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider time) : IStateTransitionService
{
    public async Task<bool> TryUpdateMissionAsync(
        Guid missionId,
        MissionStatus expected,
        MissionStatus next,
        CancellationToken ct,
        Action<Mission>? mutate = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            var mission = await db.Missions.SingleOrDefaultAsync(m => m.Id == missionId && m.Status == expected, ct);
            if (mission is null)
            {
                return false;
            }

            mission.Status = next;
            mission.Version++;
            if (next is MissionStatus.Completed or MissionStatus.Failed or MissionStatus.Halted)
            {
                mission.ClosedAt = time.GetUtcNow();
            }

            mutate?.Invoke(mission);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct);
    }

    public async Task<bool> TryUpdateSquadAsync(
        Guid squadId,
        SquadStatus expected,
        SquadStatus next,
        int expectedVersion,
        CancellationToken ct,
        Action<Squad>? mutate = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            var squad = await db.Squads.SingleOrDefaultAsync(
                s => s.Id == squadId && s.Status == expected && s.Version == expectedVersion, ct);
            if (squad is null)
            {
                return false;
            }

            squad.Status = next;
            squad.Version++;
            mutate?.Invoke(squad);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct);
    }

    public Task AddEventAsync(AppDbContext db, Guid squadId, string kind, string payload, DateTimeOffset at)
    {
        db.SquadEvents.Add(new SquadEvent
        {
            Id = Guid.NewGuid(),
            SquadId = squadId,
            Kind = kind,
            Payload = payload.Length > 8000 ? payload[..8000] + "…" : payload,
            At = at,
        });
        return Task.CompletedTask;
    }
}

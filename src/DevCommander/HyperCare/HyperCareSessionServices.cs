using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Services;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.HyperCare;

public interface IHyperCareSessionGate
{
    /// <summary>The active session (Running or BudgetHalted), or null when mode is Normal.</summary>
    Task<HyperCareSession?> GetActiveSessionAsync(CancellationToken ct);

    Task<bool> IsHyperCareActiveAsync(CancellationToken ct);
}

public sealed class HyperCareSessionGate(IDbContextFactory<AppDbContext> dbFactory) : IHyperCareSessionGate
{
    public async Task<HyperCareSession?> GetActiveSessionAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite cannot order by DateTimeOffset; order in-memory (at most one active session anyway).
        var active = await db.HyperCareSessions.AsNoTracking()
            .Where(s => s.Status == HyperCareSessionStatus.Running || s.Status == HyperCareSessionStatus.BudgetHalted)
            .ToListAsync(ct);
        return active.OrderByDescending(s => s.StartedAt).FirstOrDefault();
    }

    public async Task<bool> IsHyperCareActiveAsync(CancellationToken ct) =>
        await GetActiveSessionAsync(ct) is not null;
}

public interface IHyperCareEventLog
{
    /// <summary>Adds a durable event to the caller's transaction and emits a structured log (FR-HC-050).</summary>
    void Append(AppDbContext db, Guid sessionId, Guid? issueId, string kind, string payload, DateTimeOffset at);
}

public sealed class HyperCareEventLog(ILogger<HyperCareEventLog> logger) : IHyperCareEventLog
{
    public void Append(AppDbContext db, Guid sessionId, Guid? issueId, string kind, string payload, DateTimeOffset at)
    {
        db.HyperCareEvents.Add(new HyperCareEvent
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            IssueId = issueId,
            Kind = kind,
            Payload = payload.Length > 8000 ? payload[..8000] + "…" : payload,
            At = at,
        });
        logger.LogInformation("HyperCare {Kind} session={SessionId} issue={IssueId} {Payload}",
            kind, sessionId, issueId, payload.Length > 500 ? payload[..500] + "…" : payload);
    }
}

public interface IHyperCareBudget
{
    /// <summary>
    /// Reserves an estimate against the session budget. On insufficient funds the session flips
    /// to BudgetHalted with one budget notification (FR-HC-052) and false is returned.
    /// Only Running sessions can reserve.
    /// </summary>
    Task<bool> TryReserveAsync(Guid sessionId, decimal estimate, string what, CancellationToken ct);

    /// <summary>Replaces a prior reservation with the actual cost (refunds the unused part).</summary>
    Task ReconcileAsync(Guid sessionId, decimal? actualCostUsd, decimal reservedEstimate, CancellationToken ct);
}

public sealed class HyperCareBudget(
    IDbContextFactory<AppDbContext> dbFactory,
    IHyperCareEventLog events,
    INotificationOutbox outbox,
    TimeProvider time,
    ILogger<HyperCareBudget> logger) : IHyperCareBudget
{
    public async Task<bool> TryReserveAsync(Guid sessionId, decimal estimate, string what, CancellationToken ct)
    {
        // Fresh context per retry attempt: a busy-retry with a tracked entity would re-apply the increment.
        return await RetryOnVersionConflictAsync(() => SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var session = await db.HyperCareSessions.SingleAsync(s => s.Id == sessionId, ct);
            if (session.Status != HyperCareSessionStatus.Running)
            {
                return false;
            }

            var remaining = session.BudgetUsd - session.AccountedCostUsd;
            if (estimate > remaining)
            {
                var now = time.GetUtcNow();
                session.Status = HyperCareSessionStatus.BudgetHalted;
                session.Version++;
                events.Append(db, sessionId, null, "BudgetHalted",
                    $"Reservation '{what}' (${estimate}) exceeds remaining budget (${remaining}).", now);
                await outbox.EnqueueInTransactionAsync(db, session.ChatId, $"hc-budget:{sessionId:N}",
                    NotificationSeverity.Warning,
                    $"⛔ Hyper-Care session budget exhausted (${session.BudgetUsd}). No new triage calls or fix tracks "
                    + "will start; watchers keep counting occurrences. Use /hc_off to end the session.", now);
                await db.SaveChangesAsync(ct);
                logger.LogWarning("HyperCare session {SessionId} BudgetHalted on '{What}'", sessionId, what);
                return false;
            }

            // Count the reservation immediately so parallel reservations cannot over-allocate.
            session.AccountedCostUsd += estimate;
            session.Version++;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct), ct);
    }

    public async Task ReconcileAsync(Guid sessionId, decimal? actualCostUsd, decimal reservedEstimate, CancellationToken ct)
    {
        await RetryOnVersionConflictAsync(() => SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var session = await db.HyperCareSessions.SingleAsync(s => s.Id == sessionId, ct);
            session.AccountedCostUsd += (actualCostUsd ?? reservedEstimate) - reservedEstimate;
            session.Version++;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct), ct);
    }

    /// <summary>
    /// Watchers, fix tracks, and the coordinator all mutate the session row concurrently; its Version
    /// token turns lost updates into conflicts, which are safe to replay on a fresh context.
    /// </summary>
    private static async Task<T> RetryOnVersionConflictAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException) when (attempt < 10)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct);
            }
        }
    }
}

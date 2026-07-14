using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.Integrations.Telegram;

public sealed class TelegramInboxProcessorService(
    IDbContextFactory<AppDbContext> dbFactory,
    CommanderDispatcher dispatcher,
    TimeProvider time,
    ILogger<TelegramInboxProcessorService> logger) : BackgroundService
{
    private readonly string _leaseOwner = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var update = await TryClaimNextAsync(stoppingToken);
            if (update is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                await dispatcher.DispatchAsync(update.ChatId, update.Payload, stoppingToken);
                await MarkProcessedAsync(update.UpdateId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telegram update {UpdateId} processing failed", update.UpdateId);
                await ReleaseForRetryAsync(update.UpdateId, ex.Message, stoppingToken);
            }
        }
    }

    private async Task<TelegramUpdate?> TryClaimNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var candidates = await db.TelegramUpdates
            .Where(x => x.State == TelegramUpdateState.Pending
                     || (x.State == TelegramUpdateState.Processing && x.LeaseUntil < now))
            .OrderBy(x => x.ChatId)
            .ThenBy(x => x.UpdateId)
            .Take(100)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            var earlierOutstanding = await db.TelegramUpdates.AnyAsync(x =>
                x.ChatId == candidate.ChatId
                && x.UpdateId < candidate.UpdateId
                && x.State != TelegramUpdateState.Processed, ct);
            if (earlierOutstanding)
            {
                continue;
            }

            candidate.State = TelegramUpdateState.Processing;
            candidate.LeaseOwner = _leaseOwner;
            candidate.LeaseUntil = now.AddMinutes(2);
            await db.SaveChangesAsync(ct);
            return candidate;
        }

        return null;
    }

    private async Task MarkProcessedAsync(long updateId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var update = await db.TelegramUpdates.SingleAsync(x => x.UpdateId == updateId, ct);
        if (update.LeaseOwner != _leaseOwner)
        {
            return;
        }

        update.State = TelegramUpdateState.Processed;
        update.ProcessedAt = time.GetUtcNow();
        update.LeaseOwner = null;
        update.LeaseUntil = null;
        update.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task ReleaseForRetryAsync(long updateId, string error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var update = await db.TelegramUpdates.SingleOrDefaultAsync(x => x.UpdateId == updateId, ct);
        if (update?.LeaseOwner != _leaseOwner)
        {
            return;
        }

        update.State = TelegramUpdateState.Pending;
        update.LeaseOwner = null;
        update.LeaseUntil = null;
        update.LastError = error.Length <= 1000 ? error : error[..1000];
        await db.SaveChangesAsync(ct);
    }
}

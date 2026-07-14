using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.Integrations.Telegram;

public sealed class NotificationFlusherService(
    IDbContextFactory<AppDbContext> dbFactory,
    ITelegramMessenger messenger,
    TimeProvider time,
    ILogger<NotificationFlusherService> logger) : BackgroundService
{
    private readonly string _leaseOwner = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var notification = await TryClaimNextAsync(stoppingToken);
            if (notification is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                await messenger.SendTextAsync(notification.ChatId, notification.Body, stoppingToken);
                await MarkSentAsync(notification.Id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telegram notification {NotificationId} delivery failed", notification.Id);
                await ScheduleRetryAsync(notification.Id, ex.Message, stoppingToken);
            }
        }
    }

    private async Task<Notification?> TryClaimNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var notification = await db.Notifications
            .Where(x => (x.State == NotificationState.Pending && x.NextAttemptAt <= now)
                     || (x.State == NotificationState.Sending && x.LeaseUntil < now))
            .OrderBy(x => x.NextAttemptAt)
            .FirstOrDefaultAsync(ct);
        if (notification is null)
        {
            return null;
        }

        notification.State = NotificationState.Sending;
        notification.LeaseOwner = _leaseOwner;
        notification.LeaseUntil = now.AddMinutes(2);
        await db.SaveChangesAsync(ct);
        return notification;
    }

    private async Task MarkSentAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var notification = await db.Notifications.SingleAsync(x => x.Id == id, ct);
        if (notification.LeaseOwner != _leaseOwner)
        {
            return;
        }

        notification.State = NotificationState.Sent;
        notification.SentAt = time.GetUtcNow();
        notification.LeaseOwner = null;
        notification.LeaseUntil = null;
        notification.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task ScheduleRetryAsync(Guid id, string error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var notification = await db.Notifications.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (notification?.LeaseOwner != _leaseOwner)
        {
            return;
        }

        notification.AttemptCount++;
        notification.State = NotificationState.Pending;
        notification.NextAttemptAt = time.GetUtcNow().Add(Backoff(notification.AttemptCount));
        notification.LastError = error.Length <= 1000 ? error : error[..1000];
        notification.LeaseOwner = null;
        notification.LeaseUntil = null;
        await db.SaveChangesAsync(ct);
    }

    private static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 8))));
}

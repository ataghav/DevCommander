using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.Tests;

public sealed class TelegramInboxQueryTests
{
    [Fact]
    public async Task ClaimCandidateQuery_ReturnsPendingAgainstSqlite()
    {
        using var root = new TestDataRoot();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root.Path, "inbox.db")}")
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        db.TelegramUpdates.Add(new TelegramUpdate
        {
            UpdateId = 1,
            ChatId = 42,
            Payload = "{}",
            State = TelegramUpdateState.Pending,
            ReceivedAt = now,
        });
        db.TelegramUpdates.Add(new TelegramUpdate
        {
            UpdateId = 2,
            ChatId = 42,
            Payload = "{}",
            State = TelegramUpdateState.Processing,
            ReceivedAt = now,
            LeaseUntil = now.AddMinutes(5),
        });
        db.TelegramUpdates.Add(new TelegramUpdate
        {
            UpdateId = 3,
            ChatId = 42,
            Payload = "{}",
            State = TelegramUpdateState.Processing,
            ReceivedAt = now,
            LeaseUntil = now.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var candidates = (await db.TelegramUpdates
                .Where(x => x.State == TelegramUpdateState.Pending
                         || x.State == TelegramUpdateState.Processing)
                .OrderBy(x => x.ChatId)
                .ThenBy(x => x.UpdateId)
                .Take(100)
                .ToListAsync())
            .Where(x => x.State == TelegramUpdateState.Pending
                     || (x.State == TelegramUpdateState.Processing && x.LeaseUntil < now))
            .Select(x => x.UpdateId)
            .ToList();

        Assert.Equal(new long[] { 1, 3 }, candidates);
    }

    [Fact]
    public async Task NotificationClaimQuery_ReturnsDueAgainstSqlite()
    {
        using var root = new TestDataRoot();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root.Path, "outbox.db")}")
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            ChatId = 1,
            LogicalKey = "a",
            Body = "later",
            State = NotificationState.Pending,
            NextAttemptAt = now.AddMinutes(10),
            At = now,
        });
        var dueId = Guid.NewGuid();
        db.Notifications.Add(new Notification
        {
            Id = dueId,
            ChatId = 1,
            LogicalKey = "b",
            Body = "due",
            State = NotificationState.Pending,
            NextAttemptAt = now.AddMinutes(-1),
            At = now,
        });
        await db.SaveChangesAsync();

        var claimed = (await db.Notifications
                .Where(x => x.State == NotificationState.Pending || x.State == NotificationState.Sending)
                .ToListAsync())
            .Where(x => (x.State == NotificationState.Pending && x.NextAttemptAt <= now)
                     || (x.State == NotificationState.Sending && x.LeaseUntil < now))
            .OrderBy(x => x.NextAttemptAt)
            .FirstOrDefault();

        Assert.NotNull(claimed);
        Assert.Equal(dueId, claimed.Id);
    }
}

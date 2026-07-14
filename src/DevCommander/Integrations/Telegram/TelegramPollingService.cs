using System.Text.Json;
using DevCommander.Data;
using DevCommander.Domain.Entities;
using DevCommander.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace DevCommander.Integrations.Telegram;

public sealed class TelegramPollingService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<TelegramOptions> options,
    TimeProvider time,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    private const string OffsetKey = "telegram.update_offset";
    private readonly TelegramOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            throw new InvalidOperationException("Telegram is enabled but BotToken is not configured.");
        }

        var client = new TelegramBotClient(_options.BotToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var offset = await GetOffsetAsync(stoppingToken);
                var updates = await client.GetUpdates(
                    offset: offset,
                    timeout: 30,
                    allowedUpdates: [UpdateType.Message],
                    cancellationToken: stoppingToken);

                foreach (var update in updates.OrderBy(x => x.Id))
                {
                    await PersistUpdateAndOffsetAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telegram polling failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<int?> GetOffsetAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var value = await db.Settings.AsNoTracking()
            .Where(x => x.Key == OffsetKey)
            .Select(x => x.Value)
            .SingleOrDefaultAsync(ct);
        return int.TryParse(value, out var offset) ? offset : null;
    }

    private async Task PersistUpdateAndOffsetAsync(global::Telegram.Bot.Types.Update update, CancellationToken ct)
    {
        if (update.Message?.Chat is not { } chat || string.IsNullOrWhiteSpace(update.Message.Text))
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.TelegramUpdates.AnyAsync(x => x.UpdateId == update.Id, ct))
        {
            db.TelegramUpdates.Add(new TelegramUpdate
            {
                UpdateId = update.Id,
                ChatId = chat.Id,
                Payload = update.Message.Text,
                ReceivedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
        }

        var nextOffset = (update.Id + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var setting = await db.Settings.SingleOrDefaultAsync(x => x.Key == OffsetKey, ct);
        if (setting is null)
        {
            db.Settings.Add(new AppSetting { Key = OffsetKey, Value = nextOffset });
        }
        else if (!int.TryParse(setting.Value, out var current) || update.Id + 1 > current)
        {
            setting.Value = nextOffset;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

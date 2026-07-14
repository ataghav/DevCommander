using Telegram.Bot;

namespace DevCommander.Integrations.Telegram;

public interface ITelegramMessenger
{
    void Configure();
    void Configure(ITelegramBotClient botClient);
    Task SendTextAsync(long chatId, string text, CancellationToken ct);
}

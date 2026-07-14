using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace DevCommander.Integrations.Telegram;

public interface ITelegramMessenger
{
    void Configure();
    void Configure(ITelegramBotClient botClient);
    Task SendTextAsync(long chatId, string text, CancellationToken ct, ParseMode? parseMode = null);
}

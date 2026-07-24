using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace DevCommander.Integrations.Telegram;

public interface ITelegramMessenger
{
    void Configure();
    void Configure(ITelegramBotClient botClient);
    Task SendTextAsync(long chatId, string text, CancellationToken ct, ParseMode? parseMode = null);

    /// <summary>Sends a single message and returns its Telegram message id (first chunk's id when split).</summary>
    Task<int?> SendCardAsync(long chatId, string text, CancellationToken ct, ParseMode? parseMode = null);

    /// <summary>Edits a previously sent message in place; "message is not modified" is swallowed.</summary>
    Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken ct, ParseMode? parseMode = null);

    /// <summary>Registers the bot's slash-command menu (Telegram setMyCommands).</summary>
    Task SetMyCommandsAsync(IReadOnlyList<(string Command, string Description)> commands, CancellationToken ct);
}

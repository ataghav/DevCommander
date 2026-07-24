using Microsoft.Extensions.Options;
using DevCommander.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace DevCommander.Integrations.Telegram;

public sealed class TelegramMessenger(
    IOptions<TelegramOptions> options,
    ILogger<TelegramMessenger> _) : ITelegramMessenger
{
    private const int MaxMessageLength = 4_000;
    private readonly TelegramOptions _options = options.Value;
    private ITelegramBotClient? _client;

    public void Configure()
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            throw new InvalidOperationException("Telegram is enabled but BotToken is not configured.");
        }

        _client ??= new TelegramBotClient(_options.BotToken);
    }

    public void Configure(ITelegramBotClient botClient) =>
        _client = botClient ?? throw new ArgumentNullException(nameof(botClient));

    public async Task SendTextAsync(long chatId, string text, CancellationToken ct, ParseMode? parseMode = null)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Telegram is disabled.");
        }

        Configure();
        foreach (var chunk in SplitText(text))
        {
            if (parseMode is { } mode)
            {
                await _client!.SendMessage(chatId, chunk, parseMode: mode, cancellationToken: ct);
            }
            else
            {
                await _client!.SendMessage(chatId, chunk, cancellationToken: ct);
            }
        }
    }

    public async Task<int?> SendCardAsync(long chatId, string text, CancellationToken ct, ParseMode? parseMode = null)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Telegram is disabled.");
        }

        Configure();
        int? firstMessageId = null;
        foreach (var chunk in SplitText(text))
        {
            var message = parseMode is { } mode
                ? await _client!.SendMessage(chatId, chunk, parseMode: mode, cancellationToken: ct)
                : await _client!.SendMessage(chatId, chunk, cancellationToken: ct);
            firstMessageId ??= message.MessageId;
        }

        return firstMessageId;
    }

    public async Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken ct, ParseMode? parseMode = null)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Telegram is disabled.");
        }

        Configure();
        var body = SplitText(text).First();
        try
        {
            if (parseMode is { } mode)
            {
                await _client!.EditMessageText(chatId, messageId, body, parseMode: mode, cancellationToken: ct);
            }
            else
            {
                await _client!.EditMessageText(chatId, messageId, body, cancellationToken: ct);
            }
        }
        catch (global::Telegram.Bot.Exceptions.ApiRequestException ex)
            when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Content unchanged — nothing to do.
        }
    }

    public async Task SetMyCommandsAsync(IReadOnlyList<(string Command, string Description)> commands, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Telegram is disabled.");
        }

        Configure();
        await _client!.SetMyCommands(
            commands.Select(c => new global::Telegram.Bot.Types.BotCommand
            {
                Command = c.Command.TrimStart('/'),
                Description = c.Description,
            }),
            cancellationToken: ct);
    }

    private static IEnumerable<string> SplitText(string text)
    {
        text = string.IsNullOrEmpty(text) ? " " : text;
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(MaxMessageLength, text.Length - offset);
            if (length < text.Length - offset)
            {
                var lineBreak = text.LastIndexOf('\n', offset + length - 1, length);
                if (lineBreak >= offset)
                {
                    length = lineBreak - offset + 1;
                }
            }

            yield return text.Substring(offset, length);
            offset += length;
        }
    }
}

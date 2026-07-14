using DevCommander.Orchestration;
using DevCommander.Options;
using DevCommander.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NovaCore.Agents;
using Telegram.Bot.Types.Enums;

namespace DevCommander.Integrations.Telegram;

public sealed class CommanderDispatcher(
    IMissionCommands commands,
    [FromKeyedServices("commander")] IAgentFactory commanderFactory,
    IAgentCostTracker costs,
    ITelegramMessenger messenger,
    IOptions<TelegramOptions> options,
    ILogger<CommanderDispatcher> logger)
{
    private readonly TelegramOptions _options = options.Value;

    public async Task DispatchAsync(long chatId, string payload, CancellationToken ct)
    {
        var text = payload.Trim();
        if (text.Equals("/whoami", StringComparison.OrdinalIgnoreCase))
        {
            await messenger.SendTextAsync(chatId, $"Your chat ID is {chatId}.", ct);
            return;
        }

        if (!_options.AllowedChatIds.Contains(chatId))
        {
            logger.LogInformation("Ignoring Telegram message from unknown chat {ChatId}", chatId);
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts.FirstOrDefault()?.ToLowerInvariant();
        if (command == "/costs" && parts.Length == 1)
        {
            // HTML formatting per https://core.telegram.org/bots/api#formatting-options
            await messenger.SendTextAsync(chatId, await commands.AgentCostsAsync(ct), ct, ParseMode.Html);
            return;
        }

        var response = await DispatchAllowedAsync(chatId, text, ct);
        if (!string.IsNullOrWhiteSpace(response))
        {
            await messenger.SendTextAsync(chatId, response, ct);
        }
    }

    private async Task<string> DispatchAllowedAsync(long chatId, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts.FirstOrDefault()?.ToLowerInvariant();
        return command switch
        {
            "/missions" when parts.Length == 1 => await commands.ListMissionsAsync(chatId, ct),
            "/start" when parts.Length == 2 => await commands.StartAsync(parts[1], chatId, ct),
            "/status" when parts.Length == 2 => await commands.StatusAsync(parts[1], chatId, ct),
            "/approve" when parts.Length == 2 && Guid.TryParse(parts[1], out var approvalId)
                => await commands.ApproveAsync(approvalId, chatId, ct),
            "/stop" when parts.Length == 3 => await commands.StopAsync(parts[1], parts[2], chatId, ct),
            "/continue" when parts.Length >= 3 => await commands.ContinueAsync(
                parts[1],
                parts[2],
                string.Join(" ", parts.Skip(3)),
                chatId,
                ct),
            _ when text.StartsWith("/", StringComparison.Ordinal) => "Invalid command.",
            _ => await InvokeCommanderAsync(chatId, text, ct),
        };
    }

    private async Task<string> InvokeCommanderAsync(long chatId, string text, CancellationToken ct)
    {
        var session = $"telegram-{chatId}";
        var spec = new AgentSpec
        {
            Principal = new ExecutionPrincipal(
                "telegram",
                chatId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Dictionary<string, object?>()),
        };
        var agent = await commanderFactory.OpenAsync(session, spec, ct);
        var outcome = await agent.RunAsync(text, ct);
        await costs.RecordFromOutcomeAsync("commander", outcome, missionId: null, ct);
        return outcome switch
        {
            ExecutionOutcome<string>.Completed { Value: { Length: > 0 } value } => value,
            ExecutionOutcome<string>.Completed => "Commander completed without a response.",
            ExecutionOutcome<string>.Failed failed =>
                $"Commander failed: {failed.Error.Message}",
            ExecutionOutcome<string>.Exhausted exhausted =>
                $"Commander exhausted: {exhausted.Reason}",
            ExecutionOutcome<string>.Cancelled => "Commander was cancelled.",
            _ => "Commander completed without a response."
        };
    }
}

using Microsoft.Extensions.Options;

namespace DevCommander.Options;

public sealed class DevCommanderOptionsValidator : IValidateOptions<DevCommanderOptions>
{
    public ValidateOptionsResult Validate(string? name, DevCommanderOptions options)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(options.DataRoot)) problems.Add("DevCommander:DataRoot is required.");
        if (options.DefaultBudgetUsd <= 0) problems.Add("DevCommander:DefaultBudgetUsd must be positive.");
        if (options.DefaultMissionWallTime <= TimeSpan.Zero) problems.Add("DevCommander:DefaultMissionWallTime must be positive.");
        ValidateAgent("Commander", options.Agents.Commander, problems);
        ValidateAgent("Planner", options.Agents.Planner, problems);
        ValidateAgent("Critic", options.Agents.Critic, problems);
        foreach (var (runtimeName, executable, charge) in new[]
        {
            ("Claude", options.Runtimes.Claude.Executable, options.Runtimes.Claude.EstimatedChargeUsd),
            ("Codex", options.Runtimes.Codex.Executable, options.Runtimes.Codex.EstimatedChargeUsd),
            ("Cursor", options.Runtimes.Cursor.Executable, options.Runtimes.Cursor.EstimatedChargeUsd),
            ("OpenCode", options.Runtimes.OpenCode.Executable, options.Runtimes.OpenCode.EstimatedChargeUsd)
        })
        {
            if (string.IsNullOrWhiteSpace(executable)) problems.Add($"DevCommander:Runtimes:{runtimeName}:Executable is required.");
            if (charge <= 0) problems.Add($"DevCommander:Runtimes:{runtimeName}:EstimatedChargeUsd must be positive.");
        }
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    private static void ValidateAgent(string role, OpenAiCompatibleProviderOptions agent, List<string> problems)
    {
        if (!Uri.TryCreate(agent.BaseUrl, UriKind.Absolute, out var uri) || !uri.AbsoluteUri.EndsWith('/'))
            problems.Add($"DevCommander:Agents:{role}:BaseUrl must be an absolute URL with a trailing slash.");
        if (string.IsNullOrWhiteSpace(agent.ApiKeyEnvVar)) problems.Add($"DevCommander:Agents:{role}:ApiKeyEnvVar is required.");
        if (string.IsNullOrWhiteSpace(agent.Model)) problems.Add($"DevCommander:Agents:{role}:Model is required.");
        if (agent.InputPerMTokens is null || agent.OutputPerMTokens is null)
            problems.Add($"DevCommander:Agents:{role}:InputPerMTokens and OutputPerMTokens are required.");
    }
}

public sealed class TelegramOptionsValidator : IValidateOptions<TelegramOptions>
{
    public ValidateOptionsResult Validate(string? name, TelegramOptions options) =>
        !options.Enabled ? ValidateOptionsResult.Success :
        string.IsNullOrWhiteSpace(options.BotToken) || options.AllowedChatIds.Length == 0
            ? ValidateOptionsResult.Fail("Telegram bot token and at least one allowed chat ID are required when Telegram is enabled.")
            : ValidateOptionsResult.Success;
}

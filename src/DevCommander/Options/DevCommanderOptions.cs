using System.ComponentModel.DataAnnotations;

namespace DevCommander.Options;

public sealed class DevCommanderOptions
{
    public const string SectionName = "DevCommander";

    [Required]
    public string DataRoot { get; set; } = "";

    [Range(0.01, 1_000_000)]
    public decimal DefaultBudgetUsd { get; set; } = 5.0m;

    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan DefaultMissionWallTime { get; set; } = TimeSpan.FromHours(8);

    public AgentsOptions Agents { get; set; } = new();

    public RuntimeOptions Runtimes { get; set; } = new();

    public CostOptions Cost { get; set; } = new();
}

public sealed class AgentsOptions
{
    public OpenAiCompatibleProviderOptions Commander { get; set; } = new();
    public OpenAiCompatibleProviderOptions Planner { get; set; } = new();
    public OpenAiCompatibleProviderOptions Critic { get; set; } = new();
}

public sealed class OpenAiCompatibleProviderOptions
{
    [Required]
    public string BaseUrl { get; set; } = "";

    /// <summary>Environment variable name that holds the API key — never the key itself.</summary>
    [Required]
    public string ApiKeyEnvVar { get; set; } = "";

    [Required]
    public string Model { get; set; } = "";

    [Required]
    public string ProviderId { get; set; } = "openai-compatible";

    public int TimeoutMinutes { get; set; } = 5;

    /// <summary>Host-configured per-million-token pricing (exact when set).</summary>
    public decimal? InputPerMTokens { get; set; }

    public decimal? OutputPerMTokens { get; set; }
}

public sealed class RuntimeOptions
{
    public ClaudeRuntimeOptions Claude { get; set; } = new();
    public CodexRuntimeOptions Codex { get; set; } = new();
    public CursorRuntimeOptions Cursor { get; set; } = new();
    public OpenCodeRuntimeOptions OpenCode { get; set; } = new();
}

public sealed class ClaudeRuntimeOptions
{
    public string Executable { get; set; } = "claude";
    public decimal EstimatedChargeUsd { get; set; } = 0.50m;
}

public sealed class CodexRuntimeOptions
{
    public string Executable { get; set; } = "codex";
    public decimal EstimatedChargeUsd { get; set; } = 0.50m;
}

public sealed class CursorRuntimeOptions
{
    public string Executable { get; set; } = "agent";
    public decimal EstimatedChargeUsd { get; set; } = 0.75m;
}

public sealed class OpenCodeRuntimeOptions
{
    public string Executable { get; set; } = "opencode";
    public decimal EstimatedChargeUsd { get; set; } = 0.50m;
}

public sealed class CostOptions
{
    public decimal DefaultEstimatedChargeUsd { get; set; } = 0.50m;
}

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public bool Enabled { get; set; }

    public string BotToken { get; set; } = "";

    /// <summary>Allowlisted Telegram chat IDs.</summary>
    public long[] AllowedChatIds { get; set; } = [];
}

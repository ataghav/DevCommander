using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace DevCommander.Domain.Entities;

public sealed class Repo
{
    [Key]
    [MaxLength(128)]
    public string Id { get; set; } = "";

    [Required]
    [MaxLength(1024)]
    public string Source { get; set; } = "";

    [Required]
    [MaxLength(256)]
    public string DefaultBranch { get; set; } = "main";

    public RuntimeKind DefaultRuntime { get; set; }

    /// <summary>JSON array of verification shell commands.</summary>
    public string VerifyCommandsJson { get; set; } = "[]";

    /// <summary>JSON array of gated-op substring patterns.</summary>
    public string GatedOpsJson { get; set; } = "[]";

    public string[] GetVerifyCommands() =>
        JsonSerializer.Deserialize<string[]>(VerifyCommandsJson) ?? [];

    public void SetVerifyCommands(IEnumerable<string> commands) =>
        VerifyCommandsJson = JsonSerializer.Serialize(commands.ToArray());

    public string[] GetGatedOps() =>
        JsonSerializer.Deserialize<string[]>(GatedOpsJson) ?? [];

    public void SetGatedOps(IEnumerable<string> patterns) =>
        GatedOpsJson = JsonSerializer.Serialize(patterns.ToArray());
}

public sealed class Mission
{
    public Guid Id { get; set; }

    [Required]
    [MaxLength(128)]
    public string Slug { get; set; } = "";

    [Required]
    [MaxLength(1024)]
    public string SpecPath { get; set; } = "";

    [Required]
    [MaxLength(64)]
    public string SpecHash { get; set; } = "";

    [Required]
    public string SpecContent { get; set; } = "";

    public MissionStatus Status { get; set; } = MissionStatus.Planning;

    public decimal BudgetUsd { get; set; }

    public decimal AccountedCostUsd { get; set; }

    public DateTimeOffset Deadline { get; set; }

    public long ChatId { get; set; }

    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>JSON map of phase summaries used as downstream context.</summary>
    public string PhaseSummariesJson { get; set; } = "{}";

    public List<Squad> Squads { get; set; } = [];

    public List<TaskItem> Tasks { get; set; } = [];
}

public sealed class Squad
{
    public Guid Id { get; set; }

    public Guid MissionId { get; set; }

    public Mission Mission { get; set; } = null!;

    [Required]
    [MaxLength(128)]
    public string RepoId { get; set; } = "";

    [Required]
    [MaxLength(1024)]
    public string WorktreePath { get; set; } = "";

    [Required]
    [MaxLength(256)]
    public string Branch { get; set; } = "";

    [MaxLength(64)]
    public string? BaseCommit { get; set; }

    public RuntimeKind Runtime { get; set; }

    public SquadStatus Status { get; set; } = SquadStatus.Pending;

    public int? LastPid { get; set; }

    public DateTimeOffset? ProcessStartedAt { get; set; }

    [MaxLength(256)]
    public string? SessionId { get; set; }

    public Guid? CurrentTaskId { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>Run generation incremented on stop to reject late completions.</summary>
    public int RunGeneration { get; set; }

    [MaxLength(64)]
    public string? LastCommittedSha { get; set; }

    public bool Pushed { get; set; }

    public string? LastGuidance { get; set; }

    public List<TaskItem> Tasks { get; set; } = [];

    public List<SquadEvent> Events { get; set; } = [];
}

public sealed class TaskItem
{
    public Guid Id { get; set; }

    public Guid MissionId { get; set; }

    public Mission Mission { get; set; } = null!;

    public Guid SquadId { get; set; }

    public Squad Squad { get; set; } = null!;

    public int Phase { get; set; }

    [Required]
    public string Description { get; set; } = "";

    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    public int AttemptCount { get; set; }

    [MaxLength(128)]
    public string? LastErrorSignature { get; set; }

    [MaxLength(64)]
    public string? BaselineCommit { get; set; }

    public string? Evidence { get; set; }

    [MaxLength(64)]
    public string? CompletedCommitSha { get; set; }

    public string? PhaseSummary { get; set; }
}

public sealed class SquadEvent
{
    public Guid Id { get; set; }

    public Guid SquadId { get; set; }

    public Squad Squad { get; set; } = null!;

    [Required]
    [MaxLength(64)]
    public string Kind { get; set; } = "";

    public string Payload { get; set; } = "";

    public DateTimeOffset At { get; set; }
}

public sealed class ApprovalRequest
{
    public Guid Id { get; set; }

    public Guid MissionId { get; set; }

    public Guid SquadId { get; set; }

    public Guid TaskId { get; set; }

    public int Attempt { get; set; }

    public int CommandIndex { get; set; }

    [Required]
    [MaxLength(64)]
    public string CommandHash { get; set; } = "";

    [Required]
    public string Operation { get; set; } = "";

    public ApprovalState State { get; set; } = ApprovalState.Pending;

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public long? DecidedByChatId { get; set; }
}

public sealed class Notification
{
    public Guid Id { get; set; }

    public long ChatId { get; set; }

    [Required]
    [MaxLength(256)]
    public string LogicalKey { get; set; } = "";

    public NotificationSeverity Severity { get; set; }

    [Required]
    public string Body { get; set; } = "";

    public NotificationState State { get; set; } = NotificationState.Pending;

    public int AttemptCount { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset At { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    [MaxLength(64)]
    public string? LeaseOwner { get; set; }
}

public sealed class TelegramUpdate
{
    [Key]
    public long UpdateId { get; set; }

    public long ChatId { get; set; }

    [Required]
    public string Payload { get; set; } = "";

    public TelegramUpdateState State { get; set; } = TelegramUpdateState.Pending;

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    [MaxLength(64)]
    public string? LeaseOwner { get; set; }

    public string? LastError { get; set; }
}

public sealed class AppSetting
{
    [Key]
    [MaxLength(128)]
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";
}

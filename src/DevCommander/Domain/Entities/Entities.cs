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

/// <summary>
/// Exclusive Hyper-Care operating mode session. Process mode is derived:
/// mode is HyperCare iff a session with Status Running or BudgetHalted exists (FR-HC-001).
/// </summary>
public sealed class HyperCareSession
{
    public Guid Id { get; set; }

    public HyperCareSessionStatus Status { get; set; } = HyperCareSessionStatus.Running;

    /// <summary>Raw config JSON snapshotted at activation; watchers always read this, never the file.</summary>
    [Required]
    public string ConfigSnapshot { get; set; } = "";

    [Required]
    [MaxLength(64)]
    public string ConfigHash { get; set; } = "";

    public int MaxConcurrency { get; set; }

    public decimal BudgetUsd { get; set; }

    public decimal AccountedCostUsd { get; set; }

    public HyperCareSeverity DefaultSeverity { get; set; } = HyperCareSeverity.Medium;

    public int DefaultPriority { get; set; }

    public long ChatId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? StoppedAt { get; set; }

    public int Version { get; set; } = 1;

    public string ShortId => Id.ToString("N")[..8];
}

/// <summary>
/// Session issue keyed by (SessionId, ServiceId, Signature) per BR-HC-002. The 1:1 fix track
/// (BR-HC-004) is folded into this row (MissionId/Branch/PrUrl/LastError) instead of the SRS's
/// separate HyperCareFixTrack table — §9 states data requirements, not table layout.
/// </summary>
public sealed class HyperCareIssue
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    [Required]
    [MaxLength(12)]
    public string ShortId { get; set; } = "";

    [Required]
    [MaxLength(128)]
    public string ServiceId { get; set; } = "";

    /// <summary>Triage-normalized signature; same fault → same signature.</summary>
    [Required]
    [MaxLength(256)]
    public string Signature { get; set; } = "";

    /// <summary>Denormalized from session config for per-repo track serialization (BR-HC-006).</summary>
    [Required]
    [MaxLength(128)]
    public string RepoId { get; set; } = "";

    [Required]
    public string Summary { get; set; } = "";

    public HyperCareSeverity Severity { get; set; }

    public int Priority { get; set; }

    public int OccurrenceCount { get; set; }

    public HyperCareIssueStatus Status { get; set; } = HyperCareIssueStatus.AwaitingDecision;

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Redacted sample snippets and key attributes (bounded).</summary>
    public string AttributesJson { get; set; } = "{}";

    /// <summary>Telegram message id of the decision card, for later edits (FR-HC-021).</summary>
    public int? TelegramMessageId { get; set; }

    /// <summary>Occurrence count at last card send/edit; drives the edit-needed check.</summary>
    public int CardOccurrenceCount { get; set; }

    /// <summary>Status rendered on the last card send/edit; a mismatch re-renders (stale CTAs).</summary>
    public HyperCareIssueStatus CardStatus { get; set; }

    /// <summary>Last card send/edit/follow-up; NFR-HC-03 60s throttle.</summary>
    public DateTimeOffset? LastCardTouchAt { get; set; }

    public string? SuppressReason { get; set; }

    /// <summary>Set by /hold: this issue runs next for its repo; cleared when it leaves Running.</summary>
    public bool HoldPreferred { get; set; }

    public Guid? MissionId { get; set; }

    [MaxLength(256)]
    public string? Branch { get; set; }

    [MaxLength(1024)]
    public string? PrUrl { get; set; }

    public string? LastError { get; set; }

    public int Version { get; set; } = 1;
}

/// <summary>Append-only Hyper-Care debug trail (FR-HC-050).</summary>
public sealed class HyperCareEvent
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public Guid? IssueId { get; set; }

    [Required]
    [MaxLength(64)]
    public string Kind { get; set; } = "";

    public string Payload { get; set; } = "";

    public DateTimeOffset At { get; set; }
}

/// <summary>
/// Last-known source health per watched service, upserted every watcher cycle so /hc_status answers
/// from the DB alone and survives restarts (FR-HC-032).
/// </summary>
public sealed class HyperCareSourceHealth
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    [Required]
    [MaxLength(128)]
    public string ServiceId { get; set; } = "";

    public DateTimeOffset? LastSuccessAt { get; set; }

    public DateTimeOffset? LastErrorAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Durable cost ledger for NovaCore agents and coding CLI runs.</summary>
public sealed class AgentCostEntry
{
    public Guid Id { get; set; }

    [Required]
    [MaxLength(32)]
    public string AgentRole { get; set; } = "";

    public Guid? MissionId { get; set; }

    public decimal TotalCostUsd { get; set; }

    public decimal LlmCostUsd { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int TotalTokens { get; set; }

    /// <summary>True for coding-CLI best-effort estimates (and any non-authoritative report).</summary>
    public bool IsEstimated { get; set; }

    public DateTimeOffset At { get; set; }
}

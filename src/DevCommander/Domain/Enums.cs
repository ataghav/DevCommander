namespace DevCommander.Domain;

public enum MissionStatus
{
    Planning = 0,
    Planned = 1,
    Starting = 2,
    Running = 3,
    Blocked = 4,
    Stopped = 5,
    Completed = 6,
    Failed = 7,
    Halted = 8,
}

public enum SquadStatus
{
    Pending = 0,
    Starting = 1,
    Running = 2,
    WaitingApproval = 3,
    StalledNetwork = 4,
    Blocked = 5,
    Stopping = 6,
    Stopped = 7,
    Completed = 8,
    Failed = 9,
    Halted = 10,
}

public enum TaskStatus
{
    Pending = 0,
    Running = 1,
    WaitingApproval = 2,
    Blocked = 3,
    Done = 4,
    RetriesExhausted = 5,
}

public enum ApprovalState
{
    Pending = 0,
    Approved = 1,
    Executing = 2,
    Consumed = 3,
    Blocked = 4,
}

public enum NotificationState
{
    Pending = 0,
    Sending = 1,
    Sent = 2,
    Failed = 3,
}

public enum TelegramUpdateState
{
    Pending = 0,
    Processing = 1,
    Processed = 2,
    Failed = 3,
}

public enum RuntimeKind
{
    Claude = 0,
    Codex = 1,
    Cursor = 2,
    OpenCode = 3,
}

public enum NotificationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Critical = 3,
}

public enum FailureKind
{
    None = 0,
    TransientNetwork = 1,
    Authentication = 2,
    InvalidInvocation = 3,
    SessionUnavailable = 4,
    Cancelled = 5,
    Other = 6,
}

public enum HyperCareSessionStatus
{
    Running = 0,
    BudgetHalted = 1,
    Stopped = 2,
}

public enum HyperCareIssueStatus
{
    AwaitingDecision = 0,
    Suppressed = 1,
    Queued = 2,
    Running = 3,
    Held = 4,
    HandedOver = 5,
    Failed = 6,
    Blocked = 7,
}

/// <summary>Ordered: comparisons rely on Low &lt; Medium &lt; High &lt; Critical.</summary>
public enum HyperCareSeverity
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

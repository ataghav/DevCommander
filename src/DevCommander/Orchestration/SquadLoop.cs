using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Git;
using DevCommander.Missions;
using DevCommander.Runtimes;
using DevCommander.Services;
using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;
using TaskStatus = DevCommander.Domain.TaskStatus;

namespace DevCommander.Orchestration;

public interface ISquadLoop
{
    Task RunAsync(Guid missionId, Guid squadId, int phase, CancellationToken ct);
}

public sealed class SquadLoop(
    IDbContextFactory<AppDbContext> dbFactory,
    IGitWorkspaceService git,
    IRuntimeRegistry runtimes,
    ICostAccountingService costs,
    ICriticService critic,
    IVerifierService verifier,
    IApprovalService approvals,
    INotificationOutbox outbox,
    IRuntimePaths paths,
    TimeProvider time) : ISquadLoop
{
    public async Task RunAsync(Guid missionId, Guid squadId, int phase, CancellationToken ct)
    {
        var state = await LoadAsync(missionId, squadId, ct);
        if (state.Mission.Deadline <= time.GetUtcNow())
        {
            await HaltBudgetOrDeadlineAsync(missionId, squadId, "wall-time", ct);
            return;
        }

        if (state.Squad.Status is SquadStatus.Pending or SquadStatus.Starting or SquadStatus.WaitingApproval)
        {
            if (state.Squad.Status is SquadStatus.Pending or SquadStatus.Starting)
            {
                await git.EnsureCloneAsync(state.Repo.Id, state.Repo.Source, state.Repo.DefaultBranch, ct);
                var worktree = await git.EnsureWorktreeAsync(
                    state.Repo.Id, missionId, state.Mission.Slug, state.Squad.WorktreePath, state.Repo.DefaultBranch, ct);
                await using var init = await dbFactory.CreateDbContextAsync(ct);
                var squad = await init.Squads.SingleAsync(x => x.Id == squadId, ct);
                squad.WorktreePath = worktree.WorktreePath;
                squad.Branch = worktree.Branch;
                squad.BaseCommit = worktree.BaseCommit;
                squad.Status = SquadStatus.Running;
                squad.Version++;
                await init.SaveChangesAsync(ct);
            }
            else
            {
                // Resume after approval: only continue when an Approved request exists.
                var approved = await approvals.FindAsync(squadId, ApprovalState.Approved, ct);
                if (approved is null)
                {
                    return;
                }

                await SetSquadAsync(squadId, SquadStatus.Running, ct);
                await MarkTaskRunningAsync(approved.TaskId, ct);
            }
        }

        while (!ct.IsCancellationRequested)
        {
            state = await LoadAsync(missionId, squadId, ct);
            if (state.Mission.Deadline <= time.GetUtcNow())
            {
                await HaltBudgetOrDeadlineAsync(missionId, squadId, "wall-time", ct);
                return;
            }

            var task = state.Tasks
                .Where(x => x.Phase == phase && x.Status is not TaskStatus.Done and not TaskStatus.RetriesExhausted)
                .OrderBy(x => x.Id)
                .FirstOrDefault();
            if (task is null)
            {
                return;
            }

            if (task.Status is TaskStatus.Blocked)
            {
                return;
            }

            // Resume path: verification only after approval (skip coder/critic rework).
            if (task.Status == TaskStatus.WaitingApproval
                || await approvals.FindAsync(squadId, ApprovalState.Approved, ct) is not null)
            {
                if (!await RunVerificationAsync(missionId, squadId, task, state, startIndex: null, ct))
                {
                    return;
                }

                continue;
            }

            var baseline = task.BaselineCommit ?? await git.GetHeadShaAsync(state.Squad.WorktreePath, ct);
            var estimate = costs.GetEstimatedCharge(state.Squad.Runtime);
            if (!await costs.TryReserveAsync(missionId, estimate, ct))
            {
                await HaltBudgetOrDeadlineAsync(missionId, squadId, "budget", ct);
                return;
            }

            await MarkAttemptAsync(squadId, task.Id, baseline, ct);
            state = await LoadAsync(missionId, squadId, ct);
            task = state.Tasks.Single(x => x.Id == task.Id);
            var request = new RuntimeRunRequest(
                state.Squad.WorktreePath,
                paths.GetSquadRuntimeHome(missionId, state.Squad.RepoId),
                BuildPrompt(state.Mission, state.Squad, task),
                await costs.GetRemainingBudgetAsync(missionId, ct));
            var adapter = runtimes.Get(state.Squad.Runtime);
            var result = await RunCoderAsync(adapter, state.Squad, request, ct);
            await costs.ReconcileAsync(missionId, result.CostUsd, estimate, result.CostIsEstimated, ct);

            if (result.FailureKind == FailureKind.Cancelled)
            {
                return;
            }

            if (IsPermanentFailure(result.FailureKind))
            {
                await BlockPermanentAsync(task.Id, squadId, result.FinalMessage, ct);
                return;
            }

            if (result.FailureKind == FailureKind.TransientNetwork)
            {
                await FailAsync(task.Id, squadId, [], null, result.ExitCode, [result.FinalMessage], ct);
                await Task.Delay(ComputeBackoff(task.AttemptCount), ct);
                continue;
            }

            if (result.FailureKind != FailureKind.None || result.ExitCode != 0)
            {
                await FailAsync(task.Id, squadId, [], null, result.ExitCode, [result.FinalMessage], ct);
                continue;
            }

            var diff = await git.GetDiffAsync(state.Squad.WorktreePath, baseline, ct);
            if (string.IsNullOrWhiteSpace(diff))
            {
                await FailAsync(task.Id, squadId, ["Coder made no current-task changes."], null, null, [], ct);
                continue;
            }

            var verdict = await critic.ReviewAsync(task.Description, diff, ct);
            if (!verdict.Approved)
            {
                await FailAsync(task.Id, squadId, verdict.BlockingFindings, null, null, [verdict.Notes ?? ""], ct);
                continue;
            }

            if (!await RunVerificationAsync(missionId, squadId, task, state, startIndex: null, ct))
            {
                return;
            }
        }
    }

    private async Task<bool> RunVerificationAsync(
        Guid missionId,
        Guid squadId,
        TaskItem task,
        (Mission Mission, Squad Squad, Repo Repo, List<TaskItem> Tasks) state,
        int? startIndex,
        CancellationToken ct)
    {
        var parsed = MissionSpecParser.ParseAndValidate(
            state.Mission.SpecContent,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state.Repo.Id });
        var commands = parsed.Spec is null
            ? state.Repo.GetVerifyCommands()
            : MissionSpecParser.ResolveVerifyCommands(parsed.Spec, state.Repo.Id, state.Repo.GetVerifyCommands()).ToArray();

        var approved = await approvals.FindAsync(squadId, ApprovalState.Approved, ct);
        var resumeIndex = startIndex ?? approved?.CommandIndex;

        var verify = await verifier.VerifyAsync(
            state.Repo,
            commands,
            state.Squad.WorktreePath,
            async (index, command, hash, token) =>
            {
                var key = new ApprovalKey(missionId, squadId, task.Id, task.AttemptCount, index, hash);
                var existing = await approvals.GetAsync(key, token);
                if (existing is null)
                {
                    await approvals.RequireAsync(key, command, token);
                    await MarkWaitingApprovalAsync(task.Id, token);
                    return false;
                }

                return existing.State switch
                {
                    ApprovalState.Pending => false,
                    ApprovalState.Approved => await approvals.BeginExecutionAsync(key, token),
                    ApprovalState.Executing => false, // never auto-replay
                    ApprovalState.Consumed => true,
                    ApprovalState.Blocked => false,
                    _ => false,
                };
            },
            ct,
            resumeFromIndex: resumeIndex);

        if (verify.NeedsApproval)
        {
            return false;
        }

        if (!verify.Succeeded)
        {
            if (verify.ApprovalCommandHash is not null)
            {
                var key = new ApprovalKey(
                    missionId, squadId, task.Id, task.AttemptCount, verify.CommandIndex ?? 0, verify.ApprovalCommandHash);
                // Leave Executing as-is so reconciliation can Block it; do not Consume.
            }

            await FailAsync(task.Id, squadId, [], verify.Command, verify.ExitCode, [verify.Evidence], ct);
            return true; // continue outer loop for retry/block
        }

        // Mark any executing approval consumed after successful gated run.
        if (approved is not null)
        {
            await approvals.ConsumeAsync(
                new ApprovalKey(missionId, squadId, task.Id, task.AttemptCount, approved.CommandIndex, approved.CommandHash),
                ct);
        }

        var commit = await git.CommitAllAsync(
            state.Squad.WorktreePath, $"mission {state.Mission.Slug}: {task.Description}", ct);
        await CompleteTaskAsync(task.Id, squadId, commit, verify.Evidence, ct);
        return true;
    }

    private static bool IsPermanentFailure(FailureKind kind) =>
        kind is FailureKind.Authentication or FailureKind.InvalidInvocation or FailureKind.Other;

    private static TimeSpan ComputeBackoff(int attempt)
    {
        var ms = Math.Min(30_000, (int)(250 * Math.Pow(2, Math.Max(0, attempt - 1))));
        var jitter = Random.Shared.Next(0, 100);
        return TimeSpan.FromMilliseconds(ms + jitter);
    }

    private async Task HaltBudgetOrDeadlineAsync(Guid missionId, Guid squadId, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mission = await db.Missions.SingleAsync(x => x.Id == missionId, ct);
        var squad = await db.Squads.SingleAsync(x => x.Id == squadId, ct);
        mission.Status = MissionStatus.Halted;
        squad.Status = SquadStatus.Halted;
        squad.Version++;
        mission.Version++;
        mission.ClosedAt = time.GetUtcNow();
        await outbox.EnqueueInTransactionAsync(
            db, mission.ChatId, $"{reason}:{mission.Id:N}", NotificationSeverity.Error,
            $"Mission halted: {reason} breach.", time.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    private async Task BlockPermanentAsync(Guid taskId, Guid squadId, string message, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.SingleAsync(x => x.Id == taskId, ct);
        var squad = await db.Squads.SingleAsync(x => x.Id == squadId, ct);
        var mission = await db.Missions.SingleAsync(x => x.Id == squad.MissionId, ct);
        task.Status = TaskStatus.Blocked;
        task.LastErrorSignature = FailureSignature.Compute([], null, null, [message]);
        squad.Status = SquadStatus.Blocked;
        squad.Version++;
        mission.Status = MissionStatus.Blocked;
        mission.Version++;
        db.SquadEvents.Add(new SquadEvent
        {
            Id = Guid.NewGuid(),
            SquadId = squadId,
            Kind = "PermanentFailure",
            Payload = message.Length > 8000 ? message[..8000] : message,
            At = time.GetUtcNow(),
        });
        await outbox.EnqueueInTransactionAsync(
            db, mission.ChatId, $"blocked:{squadId:N}:{taskId:N}", NotificationSeverity.Warning,
            $"Squad {squad.RepoId} blocked: permanent failure.", time.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    private async Task<RuntimeResult> RunCoderAsync(
        IRuntimeAdapter adapter, Squad squad, RuntimeRunRequest request, CancellationToken ct)
    {
        async Task Started(ProcessStarted p, CancellationToken token)
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            var current = await db.Squads.SingleAsync(x => x.Id == squad.Id, token);
            current.LastPid = p.Pid;
            current.ProcessStartedAt = p.StartedAt;
            current.Version++;
            await db.SaveChangesAsync(token);
        }

        var result = string.IsNullOrWhiteSpace(squad.SessionId)
            ? await adapter.StartAsync(request, Started, ct)
            : await adapter.ResumeAsync(squad.SessionId, request, Started, ct);
        if (result.FailureKind == FailureKind.SessionUnavailable && !string.IsNullOrWhiteSpace(squad.SessionId))
        {
            result = await adapter.StartAsync(request, Started, ct);
        }

        if (!string.IsNullOrWhiteSpace(result.SessionId))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var current = await db.Squads.SingleAsync(x => x.Id == squad.Id, ct);
            current.SessionId = result.SessionId;
            current.Version++;
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    private async Task FailAsync(
        Guid taskId, Guid squadId, IReadOnlyList<string> findings, string? command, int? exit,
        IReadOnlyList<string> lines, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.SingleAsync(x => x.Id == taskId, ct);
        var signature = FailureSignature.Compute(findings, command, exit, lines);
        var previous = task.LastErrorSignature;
        task.LastErrorSignature = signature;
        task.Status = task.AttemptCount >= 3 ? TaskStatus.RetriesExhausted
            : previous == signature ? TaskStatus.Blocked
            : TaskStatus.Pending;
        var squad = await db.Squads.SingleAsync(x => x.Id == squadId, ct);
        var mission = await db.Missions.SingleAsync(x => x.Id == squad.MissionId, ct);
        if (task.Status == TaskStatus.RetriesExhausted)
        {
            squad.Status = SquadStatus.Failed;
            mission.Status = MissionStatus.Failed;
            mission.ClosedAt = time.GetUtcNow();
            await outbox.EnqueueInTransactionAsync(
                db, mission.ChatId, $"retries:{taskId:N}", NotificationSeverity.Error,
                $"Task retries exhausted in {squad.RepoId}.", time.GetUtcNow());
        }
        else if (task.Status == TaskStatus.Blocked)
        {
            squad.Status = SquadStatus.Blocked;
            mission.Status = MissionStatus.Blocked;
            await outbox.EnqueueInTransactionAsync(
                db, mission.ChatId, $"blocked:{taskId:N}", NotificationSeverity.Warning,
                $"Task blocked by repeated failure in {squad.RepoId}.", time.GetUtcNow());
        }

        squad.Version++;
        mission.Version++;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkAttemptAsync(Guid squadId, Guid taskId, string baseline, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.SingleAsync(x => x.Id == taskId, ct);
        task.BaselineCommit ??= baseline;
        task.AttemptCount++;
        task.Status = TaskStatus.Running;
        var squad = await db.Squads.SingleAsync(x => x.Id == squadId, ct);
        squad.CurrentTaskId = taskId;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkWaitingApprovalAsync(Guid taskId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.SingleAsync(x => x.Id == taskId, ct);
        task.Status = TaskStatus.WaitingApproval;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkTaskRunningAsync(Guid taskId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.SingleAsync(x => x.Id == taskId, ct);
        task.Status = TaskStatus.Running;
        await db.SaveChangesAsync(ct);
    }

    private async Task CompleteTaskAsync(Guid taskId, Guid squadId, string commit, string evidence, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.Tasks.SingleAsync(x => x.Id == taskId, ct);
        task.Status = TaskStatus.Done;
        task.Evidence = evidence;
        task.CompletedCommitSha = commit;
        task.PhaseSummary = $"Phase {task.Phase}: {task.Description} → {commit[..Math.Min(12, commit.Length)]}";
        var squad = await db.Squads.SingleAsync(x => x.Id == squadId, ct);
        squad.LastCommittedSha = commit;
        squad.CurrentTaskId = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task SetSquadAsync(Guid squadId, SquadStatus status, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var squad = await db.Squads.SingleAsync(x => x.Id == squadId, ct);
        squad.Status = status;
        squad.Version++;
        await db.SaveChangesAsync(ct);
    }

    private async Task<(Mission Mission, Squad Squad, Repo Repo, List<TaskItem> Tasks)> LoadAsync(
        Guid missionId, Guid squadId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mission = await db.Missions.AsNoTracking().SingleAsync(x => x.Id == missionId, ct);
        var squad = await db.Squads.AsNoTracking().SingleAsync(x => x.Id == squadId, ct);
        var repo = await db.Repos.AsNoTracking().SingleAsync(x => x.Id == squad.RepoId, ct);
        var tasks = await db.Tasks.AsNoTracking().Where(x => x.SquadId == squadId).ToListAsync(ct);
        return (mission, squad, repo, tasks);
    }

    private static string BuildPrompt(Mission mission, Squad squad, TaskItem task) =>
        $"Mission:\n{mission.SpecContent}\n\nTask:\n{task.Description}\n\nGuidance:\n{squad.LastGuidance}";
}

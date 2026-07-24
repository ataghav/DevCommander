using System.Security.Cryptography;
using System.Text;
using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.HyperCare.Watching;
using DevCommander.Missions;
using DevCommander.Options;
using DevCommander.Orchestration;
using DevCommander.Services;
using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevCommander.HyperCare;

public interface IHyperCareFixTrackService
{
    /// <summary>
    /// Drives a claimed (Running) issue: fresh claim ⇒ investigate + synthesize a mission-equivalent
    /// (FR-HC-043/046, planner bypassed per BR-HC-014); resume ⇒ parent continue semantics.
    /// </summary>
    Task StartOrResumeAsync(Guid issueId, CancellationToken ct);

    /// <summary>
    /// Finalizes a Running issue whose mission reached a terminal status: Completed ⇒ `gh pr create`
    /// handover (FR-HC-043, BR-HC-007); Failed ⇒ issue Failed; Halted ⇒ issue Blocked. Reconciles the
    /// session budget slice exactly once (the issue leaves Running exactly once).
    /// </summary>
    Task FinalizeAsync(Guid issueId, CancellationToken ct);

    /// <summary>Blocks a stalled Running issue after repeated host/git failures (bounded re-drive).</summary>
    Task BlockForHostFailureAsync(Guid issueId, string error, CancellationToken ct);
}

public sealed class HyperCareFixTrackService(
    IDbContextFactory<AppDbContext> dbFactory,
    IRuntimePaths paths,
    IHyperCareBudget budget,
    IInvestigateService investigate,
    IHyperCareEventLog events,
    INotificationOutbox outbox,
    IMissionCoordinator coordinator,
    IMissionRuntimeRegistry runtimeRegistry,
    IGitHubCli gitHub,
    IOptions<DevCommanderOptions> options,
    TimeProvider time,
    ILogger<HyperCareFixTrackService> logger) : IHyperCareFixTrackService
{
    public async Task StartOrResumeAsync(Guid issueId, CancellationToken ct)
    {
        var (issue, session, config) = await LoadAsync(issueId, ct);
        if (issue is null || session is null || config is null || issue.Status != HyperCareIssueStatus.Running)
        {
            return;
        }

        if (issue.MissionId is { } existingMissionId)
        {
            // Resume a preempted/recovered track via parent stop/continue semantics (FR-HC-042/047).
            await runtimeRegistry.ContinueSquadAsync(existingMissionId, issue.RepoId, guidance: null, ct);
            _ = Task.Run(() => coordinator.CoordinateAsync(existingMissionId, CancellationToken.None), CancellationToken.None);
            return;
        }

        if (!await budget.TryReserveAsync(session.Id, config.FixTrackBudgetUsd, $"fix track {issue.ShortId}", ct))
        {
            await TransitionAsync(issue.Id, i => i.Status = HyperCareIssueStatus.Queued, "FixTrackUnclaimed",
                "session budget could not cover the fix-track slice", ct);
            return;
        }

        InvestigateOutcome investigation;
        var reservedInvestigate = await budget.TryReserveAsync(
            session.Id, config.InvestigateEstimateUsd, $"investigate {issue.ShortId}", ct);
        if (!reservedInvestigate)
        {
            await budget.ReconcileAsync(session.Id, 0m, config.FixTrackBudgetUsd, ct);
            await TransitionAsync(issue.Id, i => i.Status = HyperCareIssueStatus.Queued, "FixTrackUnclaimed",
                "session budget could not cover the investigate call", ct);
            return;
        }

        try
        {
            investigation = await investigate.InvestigateAsync(issue, ct);
            await budget.ReconcileAsync(session.Id, investigation.ActualCostUsd, config.InvestigateEstimateUsd, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Account the failed investigate's actual cost when known; otherwise keep the estimate.
            if (ex is HyperCareAgentException { AccumulatedCostUsd: { } cost })
            {
                await budget.ReconcileAsync(session.Id, cost, config.InvestigateEstimateUsd, ct);
            }

            await budget.ReconcileAsync(session.Id, 0m, config.FixTrackBudgetUsd, ct);
            await FailIssueAsync(issue.Id, session, $"Investigate failed: {ex.Message}", ct);
            return;
        }

        Guid missionId;
        try
        {
            missionId = await SynthesizeMissionAsync(issue, session, config, investigation.Result, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Without this, the issue stays Running with no mission and every tick would
            // re-investigate and re-reserve budget.
            await budget.ReconcileAsync(session.Id, 0m, config.FixTrackBudgetUsd, ct);
            await FailIssueAsync(issue.Id, session, $"Fix-track synthesis failed: {ex.Message}", ct);
            return;
        }

        _ = Task.Run(() => coordinator.CoordinateAsync(missionId, CancellationToken.None), CancellationToken.None);
    }

    public async Task FinalizeAsync(Guid issueId, CancellationToken ct)
    {
        var (issue, session, config) = await LoadAsync(issueId, ct);
        if (issue is null || session is null || config is null
            || issue.Status != HyperCareIssueStatus.Running || issue.MissionId is not { } missionId)
        {
            return;
        }

        await using var missionDb = await dbFactory.CreateDbContextAsync(ct);
        var mission = await missionDb.Missions.AsNoTracking().SingleOrDefaultAsync(m => m.Id == missionId, ct);
        if (mission is null)
        {
            return;
        }

        switch (mission.Status)
        {
            case MissionStatus.Completed:
                // Completion implies the host pushed the branch (FR-HC-050 durable push trail).
                await using (var eventDb = await dbFactory.CreateDbContextAsync(ct))
                {
                    events.Append(eventDb, session.Id, issue.Id, "BranchPushed",
                        $"branch={issue.Branch}", time.GetUtcNow());
                    await eventDb.SaveChangesAsync(ct);
                }

                await HandoverAsync(issue, session, ct);
                break;
            case MissionStatus.Failed:
                await FailIssueAsync(issue.Id, session,
                    "Fix track mission failed (retries exhausted or blocked permanently).", ct);
                break;
            case MissionStatus.Halted:
                await BlockIssueAsync(issue.Id, session,
                    "Fix track mission halted (budget slice or wall-time exhausted).", ct);
                break;
            default:
                return; // Blocked/Stopped/WaitingApproval: parent /approve //continue //stop flows own it (FR-HC-047).
        }

        // The issue left Running exactly once above — refund the unused part of the reserved slice.
        await budget.ReconcileAsync(session.Id, mission.AccountedCostUsd, config.FixTrackBudgetUsd, ct);
    }

    private async Task HandoverAsync(HyperCareIssue issue, HyperCareSession session, CancellationToken ct)
    {
        if (issue.Branch is null || issue.PrUrl is not null)
        {
            return;
        }

        var issueId = issue.Id;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repos.AsNoTracking().SingleAsync(r => r.Id == issue.RepoId, ct);
        var cloneDir = Path.Combine(paths.ReposDir, issue.RepoId);
        try
        {
            var prUrl = await gitHub.CreatePullRequestAsync(
                cloneDir,
                issue.Branch,
                repo.DefaultBranch,
                $"Hyper-Care {issue.ShortId}: {Truncate(issue.Summary, 120)}",
                $"""
                Automated Hyper-Care fix track handover (session {session.ShortId}).

                - Service: {issue.ServiceId}
                - Severity: {issue.Severity} · Occurrences: {issue.OccurrenceCount}
                - Signature: `{issue.Signature}`

                {issue.Summary}

                Deploy and merge decisions are yours — DevCommander never deploys.
                """,
                ct);

            await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                await using var tx = await dbFactory.CreateDbContextAsync(ct);
                var row = await tx.HyperCareIssues.SingleAsync(i => i.Id == issueId, ct);
                var now = time.GetUtcNow();
                row.PrUrl = prUrl;
                row.Status = HyperCareIssueStatus.HandedOver;
                row.HoldPreferred = false;
                row.Version++;
                events.Append(tx, row.SessionId, row.Id, "IssueHandedOver", $"pr={prUrl}", now);
                await outbox.EnqueueInTransactionAsync(tx, session.ChatId, $"hc-handover:{issueId:N}",
                    NotificationSeverity.Info,
                    $"🤝 HC issue {row.ShortId} ({row.ServiceId}) handed over.\nPR: {prUrl}\nBranch: {row.Branch}\nDeploy is yours.",
                    now);
                await tx.SaveChangesAsync(ct);
                return true;
            }, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Handover failed for issue {IssueId}", issueId);
            await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                await using var tx = await dbFactory.CreateDbContextAsync(ct);
                var row = await tx.HyperCareIssues.SingleAsync(i => i.Id == issueId, ct);
                var now = time.GetUtcNow();
                row.Status = HyperCareIssueStatus.Blocked;
                row.LastError = Truncate(ex.Message, 1000);
                row.HoldPreferred = false;
                row.Version++;
                events.Append(tx, row.SessionId, row.Id, "IssueBlocked", $"gh failed: {ex.Message}", now);
                await outbox.EnqueueInTransactionAsync(tx, session.ChatId, $"hc-blocked:{issueId:N}",
                    NotificationSeverity.Error,
                    $"⚠️ HC issue {row.ShortId}: branch {row.Branch} is pushed but PR creation failed "
                    + $"({Truncate(ex.Message, 200)}). Open the PR manually.",
                    now);
                await tx.SaveChangesAsync(ct);
                return true;
            }, ct: ct);
        }
    }

    private async Task<Guid> SynthesizeMissionAsync(
        HyperCareIssue issue,
        HyperCareSession session,
        HyperCareConfig config,
        InvestigateResult investigation,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repos.AsNoTracking().SingleAsync(r => r.Id == issue.RepoId, ct);
        var service = config.Services.FirstOrDefault(s => string.Equals(s.Id, issue.ServiceId, StringComparison.OrdinalIgnoreCase));
        var runtime = service?.CoderRuntime is { Length: > 0 } preferred
            && MissionSpecParser.TryParseRuntime(preferred, out var kind)
            ? kind
            : repo.DefaultRuntime;

        // FR-HC-046: no seven-section markdown; the snapshot itself must carry all coder context,
        // because SquadLoop.BuildPrompt is just Mission + Task + Guidance. Non-markdown SpecContent
        // makes verification fall back to the repo's default verify commands.
        var specContent = $"""
            HYPER-CARE FIX TRACK — issue {issue.ShortId} on service {issue.ServiceId} (repository {issue.RepoId})
            Root cause: {investigation.RootCause}
            Issue summary: {issue.Summary}
            Severity: {issue.Severity} · Occurrences: {issue.OccurrenceCount} ({issue.FirstSeenAt:u} – {issue.LastSeenAt:u})
            Evidence (redacted): {issue.AttributesJson}
            Notes: {investigation.Notes}
            Constraints: fix only this defect; keep the change minimal and verified with the repository's test commands;
            never deploy, never touch infrastructure, never push.
            """;

        var mission = new Mission
        {
            Id = Guid.NewGuid(),
            Slug = $"hc-{issue.ShortId}",
            SpecPath = $"hypercare://issue/{issue.Id:N}",
            SpecContent = specContent,
            SpecHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(specContent)))[..64],
            Status = MissionStatus.Starting,
            BudgetUsd = config.FixTrackBudgetUsd,
            Deadline = time.GetUtcNow() + options.Value.DefaultMissionWallTime,
            ChatId = session.ChatId,
            CreatedAt = time.GetUtcNow(),
        };
        db.Missions.Add(mission);

        var squad = new Squad
        {
            Id = Guid.NewGuid(),
            MissionId = mission.Id,
            RepoId = issue.RepoId,
            WorktreePath = paths.GetWorktreePath(mission.Id, issue.RepoId),
            Branch = $"hypercare/{session.ShortId}/{issue.ShortId}",
            Runtime = runtime,
            Status = SquadStatus.Pending,
        };
        db.Squads.Add(squad);
        db.Tasks.Add(new TaskItem
        {
            Id = Guid.NewGuid(),
            MissionId = mission.Id,
            SquadId = squad.Id,
            Phase = 1,
            Description = $"[Hyper-Care issue {issue.ShortId}, service {issue.ServiceId}] {investigation.TaskDescription.Trim()}",
        });

        var issueRow = await db.HyperCareIssues.SingleAsync(i => i.Id == issue.Id, ct);
        issueRow.MissionId = mission.Id;
        issueRow.Branch = squad.Branch;
        issueRow.Version++;
        events.Append(db, session.Id, issue.Id, "FixTrackStarted",
            $"mission={mission.Slug} branch={squad.Branch} runtime={runtime}", time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return mission.Id;
    }

    private async Task FailIssueAsync(Guid issueId, HyperCareSession session, string error, CancellationToken ct)
    {
        await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var issue = await db.HyperCareIssues.SingleAsync(i => i.Id == issueId, ct);
            var now = time.GetUtcNow();
            issue.Status = HyperCareIssueStatus.Failed;
            issue.LastError = Truncate(error, 1000);
            issue.HoldPreferred = false;
            issue.Version++;
            events.Append(db, issue.SessionId, issue.Id, "IssueFailed", error, now);
            await outbox.EnqueueInTransactionAsync(db, session.ChatId, $"hc-failed:{issueId:N}",
                NotificationSeverity.Error, $"❌ HC issue {issue.ShortId} failed: {Truncate(error, 300)}", now);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct);
    }

    public async Task BlockForHostFailureAsync(Guid issueId, string error, CancellationToken ct)
    {
        var (issue, session, _) = await LoadAsync(issueId, ct);
        if (issue is null || session is null || issue.Status != HyperCareIssueStatus.Running)
        {
            return;
        }

        await BlockIssueAsync(issueId, session, error, ct);
    }

    private async Task BlockIssueAsync(Guid issueId, HyperCareSession session, string error, CancellationToken ct)
    {
        await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var issue = await db.HyperCareIssues.SingleAsync(i => i.Id == issueId, ct);
            var now = time.GetUtcNow();
            issue.Status = HyperCareIssueStatus.Blocked;
            issue.LastError = Truncate(error, 1000);
            issue.HoldPreferred = false;
            issue.Version++;
            events.Append(db, issue.SessionId, issue.Id, "IssueBlocked", error, now);
            await outbox.EnqueueInTransactionAsync(db, session.ChatId, $"hc-blocked:{issueId:N}",
                NotificationSeverity.Error, $"⚠️ HC issue {issue.ShortId} needs a human: {Truncate(error, 300)}", now);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct);
    }

    private async Task TransitionAsync(
        Guid issueId, Action<HyperCareIssue> mutate, string eventKind, string payload, CancellationToken ct)
    {
        await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var issue = await db.HyperCareIssues.SingleAsync(i => i.Id == issueId, ct);
            mutate(issue);
            issue.Version++;
            events.Append(db, issue.SessionId, issue.Id, eventKind, payload, time.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct);
    }

    private async Task<(HyperCareIssue? Issue, HyperCareSession? Session, HyperCareConfig? Config)> LoadAsync(
        Guid issueId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var issue = await db.HyperCareIssues.AsNoTracking().SingleOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null)
        {
            return (null, null, null);
        }

        var session = await db.HyperCareSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == issue.SessionId, ct);
        var config = session is null ? null : HyperCareConfigLoader.Parse(session.ConfigSnapshot, "session snapshot").Config;
        return (issue, session, config);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

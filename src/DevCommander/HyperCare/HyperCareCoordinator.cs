using System.Collections.Concurrent;
using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.HyperCare.Watching;
using DevCommander.Integrations.Telegram;
using DevCommander.Orchestration;
using DevCommander.Services;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.HyperCare;

/// <summary>
/// Deterministic Hyper-Care coordinator (ADR-HC-003): one idempotent reconcile tick drives session
/// discovery/recovery, decision-card dispatch (the issue row is its own outbox), the fix-track
/// scheduler (NFR-HC-05 + BR-HC-006), terminal-mission finalization, and held-issue requeue.
/// Crash recovery (FR-HC-006) is simply the first tick after restart.
/// </summary>
public sealed class HyperCareCoordinator(
    IDbContextFactory<AppDbContext> dbFactory,
    ServiceWatcherDeps watcherDeps,
    IHyperCareFixTrackService fixTracks,
    IMissionCoordinator missionCoordinator,
    IHyperCareEventLog events,
    INotificationOutbox outbox,
    ITelegramMessenger messenger,
    IWatcherHealthRegistry health,
    TimeProvider time,
    ILogger<HyperCareCoordinator> logger) : BackgroundService
{
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CardThrottle = TimeSpan.FromSeconds(60);

    private const int MaxRedriveFailures = 5;

    private readonly Guid _bootId = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, int> _redriveFailures = new();
    private readonly DateTimeOffset _bootAt = time.GetUtcNow();

    private Guid? _watchedSessionId;
    private CancellationTokenSource? _watcherCts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickPeriod, time);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Hyper-Care coordinator tick failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        StopWatchers();
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var session = await LoadActiveSessionAsync(ct);
        if (session is null)
        {
            if (_watchedSessionId is not null)
            {
                StopWatchers();
                _watchedSessionId = null;
            }

            // FR-HC-004: in-flight tracks of a stopped session still finish — keep reconciling them
            // to handover/failure (no watchers, no cards, no new claims).
            await ReconcileStoppedSessionTracksAsync(ct);
            return;
        }

        if (session.Id != _watchedSessionId)
        {
            StopWatchers();
            await AttachSessionAsync(session, ct);
            _watchedSessionId = session.Id;
        }

        var issues = await LoadIssuesAsync(session.Id, ct);
        await DispatchCardsAsync(session, issues, ct);
        await FinalizeTerminalMissionsAsync(session, issues, ct);
        if (session.Status == HyperCareSessionStatus.Running)
        {
            await ScheduleAsync(session, issues, ct);
        }

        await RequeueHeldAsync(session, issues, ct);
    }

    private async Task AttachSessionAsync(HyperCareSession session, CancellationToken ct)
    {
        var config = HyperCareConfigLoader.Parse(session.ConfigSnapshot, "session snapshot").Config;
        if (config is null)
        {
            logger.LogError("Session {SessionId} has an unparseable config snapshot; watchers not started", session.Id);
            return;
        }

        _watcherCts = new CancellationTokenSource();
        foreach (var service in config.Services.Where(s => s.Enabled))
        {
            var watcher = new ServiceWatcher(watcherDeps, session.Id, config, service);
            _ = Task.Run(() => watcher.RunAsync(_watcherCts.Token), CancellationToken.None);
        }

        logger.LogInformation("Hyper-Care session {SessionId} attached; {Count} watcher(s) started",
            session.Id, config.Services.Count(s => s.Enabled));

        // Restart recovery (FR-HC-006 / NFR-HC-11): re-drive in-flight fix missions and summarize once.
        // A session started by this same process run was just activated — no recovery needed.
        if (session.StartedAt >= _bootAt)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var issues = await db.HyperCareIssues.AsNoTracking()
            .Where(i => i.SessionId == session.Id)
            .ToListAsync(ct);
        var missionIds = issues
            .Where(i => i.Status == HyperCareIssueStatus.Running && i.MissionId is not null)
            .Select(i => i.MissionId!.Value)
            .ToList();
        var nonTerminal = await db.Missions.AsNoTracking()
            .Where(m => missionIds.Contains(m.Id)
                && m.Status != MissionStatus.Completed
                && m.Status != MissionStatus.Failed
                && m.Status != MissionStatus.Halted)
            .Select(m => m.Id)
            .ToListAsync(ct);
        foreach (var missionId in nonTerminal)
        {
            _ = Task.Run(() => missionCoordinator.CoordinateAsync(missionId, CancellationToken.None), CancellationToken.None);
        }

        var now = time.GetUtcNow();
        events.Append(db, session.Id, null, "SessionRecovered",
            $"issues={issues.Count} inFlightMissions={nonTerminal.Count}", now);
        await outbox.EnqueueInTransactionAsync(db, session.ChatId,
            $"hc-recovery:{session.Id:N}:{_bootId:N}", NotificationSeverity.Info,
            $"🔁 Hyper-Care session {session.ShortId} recovered after restart: watchers resumed, "
            + $"{issues.Count(i => i.Status == HyperCareIssueStatus.AwaitingDecision)} awaiting decision, "
            + $"{issues.Count(i => i.Status == HyperCareIssueStatus.Queued)} queued, "
            + $"{nonTerminal.Count} in-flight fix track(s) reconciled. No completed work is redone.", now);
        await db.SaveChangesAsync(ct);
    }

    private async Task DispatchCardsAsync(
        HyperCareSession session, IReadOnlyList<HyperCareIssue> issues, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var issue in issues)
        {
            // SQLite can't compare DateTimeOffset in queries, so the throttle check runs in memory.
            var throttleOk = issue.LastCardTouchAt is null || now - issue.LastCardTouchAt >= CardThrottle;
            if (!throttleOk)
            {
                continue;
            }

            try
            {
                if (issue.Status == HyperCareIssueStatus.AwaitingDecision && issue.TelegramMessageId is null)
                {
                    // Initial decision card (FR-HC-021/022); failures simply retry on a later tick.
                    var messageId = await messenger.SendCardAsync(
                        session.ChatId, HyperCareCards.FormatIssueCard(issue, session.DefaultSeverity), ct);
                    await TouchCardAsync(issue.Id, messageId, issue.OccurrenceCount, issue.Status, "CardSent", ct);
                }
                else if (issue.TelegramMessageId is { } existingId
                    && (issue.CardOccurrenceCount != issue.OccurrenceCount || issue.CardStatus != issue.Status))
                {
                    // Repeat occurrences or a status change (stale go/no-go CTAs): at most one
                    // in-place edit per issue per 60s (NFR-HC-03).
                    await messenger.EditMessageTextAsync(
                        session.ChatId, existingId, HyperCareCards.FormatIssueCard(issue, session.DefaultSeverity), ct);
                    await TouchCardAsync(issue.Id, existingId, issue.OccurrenceCount, issue.Status, "CardEdited", ct);
                }
                else if (issue.TelegramMessageId is null
                    && issue.Status != HyperCareIssueStatus.AwaitingDecision
                    && issue.CardOccurrenceCount != 0
                    && issue.CardOccurrenceCount != issue.OccurrenceCount)
                {
                    // FR-HC-021 fallback: id lost — throttled count-only follow-up, never a new go/no-go prompt.
                    await messenger.SendTextAsync(
                        session.ChatId,
                        $"HC issue {issue.ShortId}: seen {issue.OccurrenceCount}× (no card to edit).", ct);
                    await TouchCardAsync(issue.Id, null, issue.OccurrenceCount, issue.Status, "CardFollowUp", ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Card dispatch failed for issue {IssueId}", issue.Id);
            }
        }
    }

    private async Task FinalizeTerminalMissionsAsync(
        HyperCareSession session, IReadOnlyList<HyperCareIssue> issues, CancellationToken ct)
    {
        // Crash between claim and mission synthesis: re-drive the claim (idempotent by status guard).
        foreach (var issue in issues.Where(i => i.Status == HyperCareIssueStatus.Running && i.MissionId is null))
        {
            SpawnTracked(issue.Id, () => fixTracks.StartOrResumeAsync(issue.Id, CancellationToken.None));
        }

        var running = issues
            .Where(i => i.Status == HyperCareIssueStatus.Running && i.MissionId is not null)
            .ToList();
        if (running.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var missionIds = running.Select(i => i.MissionId!.Value).ToList();
        var terminal = await db.Missions.AsNoTracking()
            .Where(m => missionIds.Contains(m.Id)
                && (m.Status == MissionStatus.Completed
                    || m.Status == MissionStatus.Failed
                    || m.Status == MissionStatus.Halted))
            .Select(m => m.Id)
            .ToListAsync(ct);
        foreach (var issue in running)
        {
            var issueId = issue.Id;
            if (terminal.Contains(issue.MissionId!.Value))
            {
                SpawnTracked(issueId, () => fixTracks.FinalizeAsync(issueId, CancellationToken.None));
            }
            else
            {
                // Re-drive the mission coordinator: heals a faulted fire-and-forget run (e.g. a push
                // exception) — CoordinateAsync is idempotent and returns fast while work is in flight.
                // Persistent host/git failures are bounded: after several consecutive faults the issue
                // is Blocked with the error instead of retrying forever.
                var missionId = issue.MissionId!.Value;
                SpawnTracked(issueId, async () =>
                {
                    try
                    {
                        await missionCoordinator.CoordinateAsync(missionId, CancellationToken.None);
                        _redriveFailures.TryRemove(issueId, out _);
                    }
                    catch (Exception ex)
                    {
                        var failures = _redriveFailures.AddOrUpdate(issueId, 1, (_, n) => n + 1);
                        logger.LogWarning(ex, "Mission re-drive failed for issue {IssueId} (attempt {Attempt})",
                            issueId, failures);
                        if (failures >= MaxRedriveFailures)
                        {
                            _redriveFailures.TryRemove(issueId, out _);
                            await fixTracks.BlockForHostFailureAsync(issueId,
                                $"Fix track stalled after {failures} host/git failures: {ex.Message}",
                                CancellationToken.None);
                        }
                    }
                });
            }
        }
    }

    private async Task ReconcileStoppedSessionTracksAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var running = await db.HyperCareIssues.AsNoTracking()
            .Where(i => i.Status == HyperCareIssueStatus.Running)
            .ToListAsync(ct);
        if (running.Count == 0)
        {
            return;
        }

        var sessionIds = running.Select(i => i.SessionId).Distinct().ToList();
        var stopped = await db.HyperCareSessions.AsNoTracking()
            .Where(s => sessionIds.Contains(s.Id) && s.Status == HyperCareSessionStatus.Stopped)
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var issue in running.Where(i => stopped.Contains(i.SessionId)))
        {
            if (issue.MissionId is { } missionId)
            {
                var terminal = await db.Missions.AsNoTracking().AnyAsync(
                    m => m.Id == missionId
                        && (m.Status == MissionStatus.Completed
                            || m.Status == MissionStatus.Failed
                            || m.Status == MissionStatus.Halted), ct);
                if (terminal)
                {
                    SpawnTracked(issue.Id, () => fixTracks.FinalizeAsync(issue.Id, CancellationToken.None));
                }
            }
            else
            {
                // Claimed but never synthesized: StartOrResumeAsync demotes it to Queued because the
                // stopped session can no longer reserve budget — it freezes there (BR-HC-017).
                SpawnTracked(issue.Id, () => fixTracks.StartOrResumeAsync(issue.Id, CancellationToken.None));
            }
        }
    }

    private async Task ScheduleAsync(
        HyperCareSession session, IReadOnlyList<HyperCareIssue> issues, CancellationToken ct)
    {
        var runningCount = issues.Count(i => i.Status == HyperCareIssueStatus.Running);
        if (runningCount >= session.MaxConcurrency)
        {
            return;
        }

        var occupiedRepos = issues
            .Where(i => i.Status == HyperCareIssueStatus.Running)
            .Select(i => i.RepoId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferredByRepo = issues
            .Where(i => i.Status == HyperCareIssueStatus.Queued && i.HoldPreferred)
            .GroupBy(i => i.RepoId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var eligible = issues
            .Where(i => i.Status == HyperCareIssueStatus.Queued)
            .Where(i => !occupiedRepos.Contains(i.RepoId))
            .Where(i => !preferredByRepo.TryGetValue(i.RepoId, out var preferredId) || preferredId == i.Id)
            .OrderByDescending(i => i.HoldPreferred)
            .ThenByDescending(i => i.Priority)
            .ThenByDescending(i => i.Severity)
            .ThenBy(i => i.FirstSeenAt)
            .ToList();

        foreach (var candidate in eligible)
        {
            if (runningCount >= session.MaxConcurrency)
            {
                break;
            }

            if (occupiedRepos.Contains(candidate.RepoId))
            {
                continue;
            }

            if (!await TryClaimAsync(session, candidate, ct))
            {
                continue;
            }

            runningCount++;
            occupiedRepos.Add(candidate.RepoId);
            SpawnTracked(candidate.Id, () => fixTracks.StartOrResumeAsync(candidate.Id, CancellationToken.None));
        }
    }

    private async Task<bool> TryClaimAsync(HyperCareSession session, HyperCareIssue candidate, CancellationToken ct)
    {
        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var issue = await db.HyperCareIssues.SingleOrDefaultAsync(
                i => i.Id == candidate.Id
                    && i.Status == HyperCareIssueStatus.Queued
                    && i.Version == candidate.Version, ct);
            if (issue is null)
            {
                return false;
            }

            issue.Status = HyperCareIssueStatus.Running;
            issue.Version++;
            events.Append(db, session.Id, issue.Id, "IssueClaimed",
                $"repo={issue.RepoId} priority={issue.Priority} severity={issue.Severity}", time.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct);
    }

    private async Task RequeueHeldAsync(
        HyperCareSession session, IReadOnlyList<HyperCareIssue> issues, CancellationToken ct)
    {
        // A Held issue returns to the queue once its repo's preferred issue is gone (status model §9),
        // i.e. no same-repo issue still carries HoldPreferred (cleared when a track leaves Running).
        var reposWithPreference = issues
            .Where(i => i.HoldPreferred)
            .Select(i => i.RepoId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var held in issues.Where(i => i.Status == HyperCareIssueStatus.Held
            && !reposWithPreference.Contains(i.RepoId)))
        {
            await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var issue = await db.HyperCareIssues.SingleOrDefaultAsync(
                    i => i.Id == held.Id && i.Status == HyperCareIssueStatus.Held, ct);
                if (issue is null)
                {
                    return false;
                }

                issue.Status = HyperCareIssueStatus.Queued;
                issue.Version++;
                events.Append(db, session.Id, issue.Id, "IssueRequeued", "preferred work finished", time.GetUtcNow());
                await db.SaveChangesAsync(ct);
                return true;
            }, ct: ct);
        }
    }

    private void SpawnTracked(Guid issueId, Func<Task> work)
    {
        if (!_inFlight.TryAdd(issueId, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Hyper-Care fix-track work failed for issue {IssueId}", issueId);
            }
            finally
            {
                _inFlight.TryRemove(issueId, out _);
            }
        }, CancellationToken.None);
    }

    private async Task TouchCardAsync(
        Guid issueId, int? messageId, int occurrenceCount, HyperCareIssueStatus status, string eventKind, CancellationToken ct)
    {
        await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var issue = await db.HyperCareIssues.SingleAsync(i => i.Id == issueId, ct);
            issue.TelegramMessageId ??= messageId;
            issue.CardOccurrenceCount = occurrenceCount;
            issue.CardStatus = status;
            issue.LastCardTouchAt = time.GetUtcNow();
            issue.Version++;
            events.Append(db, issue.SessionId, issue.Id, eventKind,
                $"messageId={issue.TelegramMessageId} occurrences={occurrenceCount}", time.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return true;
        }, ct: ct);
    }

    private async Task<HyperCareSession?> LoadActiveSessionAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite cannot order by DateTimeOffset; order in-memory (at most one active session anyway).
        var active = await db.HyperCareSessions.AsNoTracking()
            .Where(s => s.Status == HyperCareSessionStatus.Running || s.Status == HyperCareSessionStatus.BudgetHalted)
            .ToListAsync(ct);
        return active.OrderByDescending(s => s.StartedAt).FirstOrDefault();
    }

    private async Task<IReadOnlyList<HyperCareIssue>> LoadIssuesAsync(Guid sessionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.HyperCareIssues.AsNoTracking()
            .Where(i => i.SessionId == sessionId)
            .ToListAsync(ct);
    }

    /// <summary>Stops any running watchers (used on host shutdown and by tests).</summary>
    public void Shutdown() => StopWatchers();

    private void StopWatchers()
    {
        if (_watcherCts is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
            _watcherCts = null;
            health.Reset();
            logger.LogInformation("Hyper-Care watchers stopped");
        }
    }
}

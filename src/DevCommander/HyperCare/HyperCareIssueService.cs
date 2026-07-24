using System.Text.Json;
using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Orchestration;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.HyperCare;

public interface IHyperCareIssueService
{
    /// <summary>
    /// Creates or updates the issue keyed by (session, service, signature) (BR-HC-002).
    /// Existing issues — terminal included — only aggregate occurrences; they never reopen (FR-HC-024).
    /// </summary>
    Task<HyperCareIssue> UpsertOccurrenceAsync(
        HyperCareSession session,
        string serviceId,
        string signature,
        string summary,
        string sampleSnippet,
        string repoId,
        int occurrences,
        CancellationToken ct);

    Task<string> GoAsync(HyperCareSession session, string shortId, HyperCareSeverity? severity, CancellationToken ct);
    Task<string> NoGoAsync(HyperCareSession session, string shortId, CancellationToken ct);
    Task<string> SetSeverityAsync(HyperCareSession session, string shortId, HyperCareSeverity severity, CancellationToken ct);
    Task<string> SetPriorityAsync(HyperCareSession session, string shortId, int priority, CancellationToken ct);
    Task<string> HoldAsync(HyperCareSession session, string shortId, CancellationToken ct);
    Task<string> UnholdAsync(HyperCareSession session, string shortId, CancellationToken ct);
}

public sealed class HyperCareIssueService(
    IDbContextFactory<AppDbContext> dbFactory,
    IHyperCareEventLog events,
    IMissionRuntimeRegistry runtimeRegistry,
    TimeProvider time) : IHyperCareIssueService
{
    private const int MaxSamples = 3;
    private const int MaxSampleChars = 2000;

    private static readonly HyperCareIssueStatus[] Terminal =
    [
        HyperCareIssueStatus.Suppressed, HyperCareIssueStatus.HandedOver, HyperCareIssueStatus.Failed,
    ];

    public async Task<HyperCareIssue> UpsertOccurrenceAsync(
        HyperCareSession session,
        string serviceId,
        string signature,
        string summary,
        string sampleSnippet,
        string repoId,
        int occurrences,
        CancellationToken ct)
    {
        occurrences = Math.Max(1, occurrences);
        // Fresh context per retry attempt: a busy-retry with tracked entities would re-apply increments.
        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = time.GetUtcNow();
            var existing = await db.HyperCareIssues.SingleOrDefaultAsync(
                i => i.SessionId == session.Id && i.ServiceId == serviceId && i.Signature == signature, ct);
            if (existing is not null)
            {
                existing.OccurrenceCount += occurrences;
                existing.LastSeenAt = now;
                existing.AttributesJson = AppendSample(existing.AttributesJson, sampleSnippet);
                existing.Version++;
                await db.SaveChangesAsync(ct);
                return existing;
            }

            var id = Guid.NewGuid();
            var issue = new HyperCareIssue
            {
                Id = id,
                SessionId = session.Id,
                ShortId = id.ToString("N")[..8],
                ServiceId = serviceId,
                Signature = signature,
                RepoId = repoId,
                Summary = summary,
                Severity = session.DefaultSeverity,
                Priority = session.DefaultPriority,
                OccurrenceCount = occurrences,
                Status = HyperCareIssueStatus.AwaitingDecision,
                FirstSeenAt = now,
                LastSeenAt = now,
                AttributesJson = AppendSample("{}", sampleSnippet),
            };
            db.HyperCareIssues.Add(issue);
            events.Append(db, session.Id, id, "IssueCreated",
                $"service={serviceId} signature={signature} summary={summary}", now);
            try
            {
                await db.SaveChangesAsync(ct);
                return issue;
            }
            catch (DbUpdateException)
            {
                // Unique-index race with a concurrent watcher: fold into the winner's row.
                db.ChangeTracker.Clear();
                var winner = await db.HyperCareIssues.SingleAsync(
                    i => i.SessionId == session.Id && i.ServiceId == serviceId && i.Signature == signature, ct);
                winner.OccurrenceCount += occurrences;
                winner.LastSeenAt = now;
                winner.Version++;
                await db.SaveChangesAsync(ct);
                return winner;
            }
        }, ct: ct);
    }

    public Task<string> GoAsync(HyperCareSession session, string shortId, HyperCareSeverity? severity, CancellationToken ct) =>
        WithIssueAsync(session, shortId, ct, (db, issue, now) =>
        {
            switch (issue.Status)
            {
                case HyperCareIssueStatus.Suppressed:
                    return $"Issue {issue.ShortId} was suppressed by /nogo; it cannot be accepted in this session.";
                case HyperCareIssueStatus.HandedOver or HyperCareIssueStatus.Failed:
                    return $"Issue {issue.ShortId} is terminal ({issue.Status}); /go is a no-op.";
                case HyperCareIssueStatus.Queued or HyperCareIssueStatus.Running
                    or HyperCareIssueStatus.Held or HyperCareIssueStatus.Blocked:
                    // Idempotent: never a second fix track (FR-HC-034 / BR-HC-004).
                    return $"Issue {issue.ShortId} is already accepted ({issue.Status}); one fix track only.";
            }

            if (severity is { } s)
            {
                issue.Severity = s;
            }

            issue.Status = HyperCareIssueStatus.Queued;
            issue.Version++;
            events.Append(db, session.Id, issue.Id, "IssueQueued",
                $"go severity={issue.Severity} priority={issue.Priority}", now);
            return $"✅ Issue {issue.ShortId} accepted (severity {Fmt(issue.Severity)}); queued for a fix track.";
        });

    public Task<string> NoGoAsync(HyperCareSession session, string shortId, CancellationToken ct) =>
        WithIssueAsync(session, shortId, ct, (db, issue, now) =>
        {
            if (issue.Status != HyperCareIssueStatus.AwaitingDecision)
            {
                return $"Issue {issue.ShortId} is {issue.Status}; /nogo only applies before /go.";
            }

            issue.Status = HyperCareIssueStatus.Suppressed;
            issue.SuppressReason = "operator nogo";
            issue.Version++;
            events.Append(db, session.Id, issue.Id, "IssueSuppressed", "nogo", now);
            return $"🔕 Issue {issue.ShortId} suppressed for this session; occurrences keep counting silently.";
        });

    public Task<string> SetSeverityAsync(HyperCareSession session, string shortId, HyperCareSeverity severity, CancellationToken ct) =>
        WithIssueAsync(session, shortId, ct, (db, issue, now) =>
        {
            if (Terminal.Contains(issue.Status))
            {
                return $"Issue {issue.ShortId} is terminal ({issue.Status}); severity is frozen.";
            }

            issue.Severity = severity;
            issue.Version++;
            events.Append(db, session.Id, issue.Id, "SeverityChanged", $"severity={severity}", now);
            return $"Issue {issue.ShortId} severity set to {Fmt(severity)}.";
        });

    public Task<string> SetPriorityAsync(HyperCareSession session, string shortId, int priority, CancellationToken ct) =>
        WithIssueAsync(session, shortId, ct, (db, issue, now) =>
        {
            if (Terminal.Contains(issue.Status))
            {
                return $"Issue {issue.ShortId} is terminal ({issue.Status}); priority is frozen.";
            }

            issue.Priority = priority;
            issue.Version++;
            events.Append(db, session.Id, issue.Id, "PriorityChanged", $"priority={priority}", now);
            return $"Issue {issue.ShortId} priority set to {priority}.";
        });

    public async Task<string> HoldAsync(HyperCareSession session, string shortId, CancellationToken ct)
    {
        // Stop the same-repo running track BEFORE marking anything Held: transitioning first would
        // free the repo slot while the old track's squad is still live (concurrent same-repo tracks).
        HyperCareIssue? preferredSnapshot;
        HyperCareIssue? runningSnapshot;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            preferredSnapshot = await db.HyperCareIssues.AsNoTracking().SingleOrDefaultAsync(
                i => i.SessionId == session.Id && i.ShortId == shortId.ToLowerInvariant(), ct);
            if (preferredSnapshot is null)
            {
                return $"Unknown issue id '{shortId}'.";
            }

            if (preferredSnapshot.Status != HyperCareIssueStatus.Queued)
            {
                return $"Issue {preferredSnapshot.ShortId} is {preferredSnapshot.Status}; /hold applies to queued issues.";
            }

            runningSnapshot = await db.HyperCareIssues.AsNoTracking().FirstOrDefaultAsync(
                i => i.SessionId == session.Id
                    && i.RepoId == preferredSnapshot.RepoId
                    && i.Status == HyperCareIssueStatus.Running
                    && i.Id != preferredSnapshot.Id, ct);
        }

        var stopped = false;
        if (runningSnapshot?.MissionId is { } missionId)
        {
            stopped = await runtimeRegistry.StopSquadAsync(missionId, runningSnapshot.RepoId, ct);
        }

        return await WithIssueAsync(session, shortId, ct, (db, issue, now) =>
        {
            if (issue.Status != HyperCareIssueStatus.Queued)
            {
                return $"Issue {issue.ShortId} is {issue.Status}; /hold applies to queued issues.";
            }

            // Only one preference per repo: a repeated /hold moves the preference instead of stacking it.
            foreach (var other in db.HyperCareIssues
                .Where(i => i.SessionId == session.Id && i.RepoId == issue.RepoId
                    && i.HoldPreferred && i.Id != issue.Id)
                .ToList())
            {
                other.HoldPreferred = false;
                other.Version++;
            }

            issue.HoldPreferred = true;
            issue.Version++;
            events.Append(db, session.Id, issue.Id, "HoldPreferred", $"repo={issue.RepoId}", now);
            if (runningSnapshot is null)
            {
                return $"⏫ Issue {issue.ShortId} now runs next for repo {issue.RepoId}.";
            }

            var running = db.HyperCareIssues.SingleOrDefault(
                i => i.Id == runningSnapshot.Id && i.Status == HyperCareIssueStatus.Running);
            if (running is null || !stopped)
            {
                // Track is between claim and squad start (or waiting on approval): its coder is not
                // paused, so it keeps its slot; the preference still applies once it finishes.
                return $"⏫ Issue {issue.ShortId} preferred for repo {issue.RepoId}; the current track "
                    + $"({runningSnapshot.ShortId}) could not be paused right now and will finish first.";
            }

            running.Status = HyperCareIssueStatus.Held;
            running.Version++;
            events.Append(db, session.Id, running.Id, "IssueHeld", $"preempted-by={issue.ShortId}", now);
            return $"⏫ Issue {issue.ShortId} preferred; running issue {running.ShortId} paused (Held). "
                + $"/unhold {running.ShortId} to requeue it.";
        });
    }

    public Task<string> UnholdAsync(HyperCareSession session, string shortId, CancellationToken ct) =>
        WithIssueAsync(session, shortId, ct, (db, issue, now) =>
        {
            if (issue.Status != HyperCareIssueStatus.Held)
            {
                return $"Issue {issue.ShortId} is {issue.Status}; /unhold applies to held issues.";
            }

            issue.Status = HyperCareIssueStatus.Queued;
            issue.Version++;
            events.Append(db, session.Id, issue.Id, "IssueRequeued", "unhold", now);
            return $"Issue {issue.ShortId} returned to the queue.";
        });

    private async Task<string> WithIssueAsync(
        HyperCareSession session,
        string shortId,
        CancellationToken ct,
        Func<AppDbContext, HyperCareIssue, DateTimeOffset, string> apply)
    {
        if (session.Status == HyperCareSessionStatus.Stopped)
        {
            return "Hyper-Care session is stopped; decision commands are rejected.";
        }

        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var issue = await db.HyperCareIssues.SingleOrDefaultAsync(
                i => i.SessionId == session.Id && i.ShortId == shortId.ToLowerInvariant(), ct);
            if (issue is null)
            {
                return $"Unknown issue id '{shortId}'.";
            }

            var reply = apply(db, issue, time.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return reply;
        }, ct: ct);
    }

    private static string Fmt(HyperCareSeverity severity) => severity.ToString().ToLowerInvariant();

    private static string AppendSample(string attributesJson, string sampleSnippet)
    {
        if (string.IsNullOrWhiteSpace(sampleSnippet))
        {
            return attributesJson;
        }

        List<string> samples = [];
        try
        {
            using var doc = JsonDocument.Parse(attributesJson);
            if (doc.RootElement.TryGetProperty("samples", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                samples = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Rebuild from scratch on malformed attributes.
        }

        var bounded = sampleSnippet.Length > MaxSampleChars ? sampleSnippet[..MaxSampleChars] + "…" : sampleSnippet;
        if (!samples.Contains(bounded))
        {
            samples.Add(bounded);
            if (samples.Count > MaxSamples)
            {
                samples.RemoveAt(0);
            }
        }

        return JsonSerializer.Serialize(new { samples });
    }
}

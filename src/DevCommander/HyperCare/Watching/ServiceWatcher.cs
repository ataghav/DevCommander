using System.Text.RegularExpressions;
using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.HyperCare.Watching;

/// <summary>Shared dependencies for per-service watchers (one DI singleton, many watcher instances).</summary>
public sealed record ServiceWatcherDeps(
    IDbContextFactory<AppDbContext> DbFactory,
    IGrafanaClient Grafana,
    IAzureCliRunner Azure,
    IHyperCareBudget Budget,
    ITriageService Triage,
    IHyperCareIssueService Issues,
    IHyperCareEventLog Events,
    IWatcherHealthRegistry Health,
    TimeProvider Time,
    ILogger<ServiceWatcher> Logger);

/// <summary>
/// Hybrid watcher for one service (FR-HC-010): Grafana + az ingest → redact → imperative filter →
/// local-signature grouping → triage LLM only for unseen signatures (BR-HC-010, NFR-HC-02).
/// Runs as a coordinator-owned task; dies with the session.
/// </summary>
public sealed class ServiceWatcher(
    ServiceWatcherDeps deps,
    Guid sessionId,
    HyperCareConfig config,
    ServiceConfig service)
{
    private static readonly TimeSpan MaxLookback = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    // localSignature → triage-normalized signature (null = triage-rejected). Per-service, session-lifetime.
    private readonly Dictionary<string, string?> _signatureCache = [];
    private readonly IReadOnlyList<Regex> _include = CandidateFilter.CompileAll(service.Include);
    private readonly IReadOnlyList<Regex> _exclude = CandidateFilter.CompileAll(service.Exclude);
    private readonly IReadOnlyList<Regex> _redaction = CandidateFilter.CompileAll(config.Redaction.Patterns);

    // One cursor per Grafana query: a failing sibling must not rewind (and double-count) the others.
    private readonly Dictionary<string, DateTimeOffset> _cursors = [];
    private int _consecutiveErrors;
    private bool _hydrated;

    public async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(config.PollIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            if (!await PollOnceAsync(ct))
            {
                return;
            }

            var delay = _consecutiveErrors > 0
                ? Min(TimeSpan.FromSeconds(interval.TotalSeconds * Math.Pow(2, Math.Min(_consecutiveErrors, 5))), MaxBackoff)
                : interval;
            try
            {
                await Task.Delay(delay, deps.Time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One ingest→filter→triage→upsert cycle. Returns false when the session is gone/stopped.</summary>
    public async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var session = await LoadSessionAsync(ct);
            if (session is null || session.Status == HyperCareSessionStatus.Stopped)
            {
                return false;
            }

            await RunCycleAsync(session, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _consecutiveErrors++;
            deps.Health.MarkError(service.Id, ex.Message);
            deps.Logger.LogWarning(ex, "Watcher cycle failed for {ServiceId}", service.Id);
            await AppendEventAsync("WatcherError", ex.Message, ct);
            await UpsertHealthAsync(ex.Message, deps.Time.GetUtcNow(), ct);
        }

        return true;
    }

    private async Task RunCycleAsync(HyperCareSession session, CancellationToken ct)
    {
        var now = deps.Time.GetUtcNow();
        await HydrateSignatureCacheAsync(session.Id, ct);
        var rawLines = new List<string>();
        var azureLines = new List<string>();
        var sourceErrors = new List<string>();

        var token = Environment.GetEnvironmentVariable(config.Grafana.TokenEnvVar) ?? "";
        foreach (var query in service.GrafanaQueries)
        {
            var cursor = _cursors.TryGetValue(query.Name, out var stored)
                ? stored
                : now - TimeSpan.FromSeconds(config.PollIntervalSeconds);
            var from = Max(cursor, now - MaxLookback);
            try
            {
                var json = await deps.Grafana.QueryAsync(config.Grafana.BaseUrl, token, query, from, now, ct);
                rawLines.AddRange(CandidateFilter.ExtractStringLeaves(json));
                _cursors[query.Name] = now;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Cursor untouched: this window is retried after backoff without rewinding siblings.
                sourceErrors.Add($"grafana '{query.Name}': {ex.Message}");
            }
        }

        foreach (var check in service.AzureChecks)
        {
            try
            {
                var (ok, evidence) = await deps.Azure.RunCheckAsync(check, ct);
                if (!ok)
                {
                    // Explicit checks are inherently candidates; they bypass the include/exclude filters.
                    azureLines.Add(evidence);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                sourceErrors.Add($"azure '{check.Name}': {ex.Message}");
            }
        }

        var redacted = rawLines.Select(line => CandidateFilter.Redact(line, _redaction)).ToList();
        var candidates = CandidateFilter.Apply(redacted, _include, _exclude)
            .Concat(azureLines.Select(line => CandidateFilter.Redact(line, _redaction)))
            .ToList();
        var groups = candidates
            .GroupBy(CandidateFilter.LocalSignature)
            .ToList();

        var triaged = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (_signatureCache.TryGetValue(group.Key, out var known))
            {
                if (known is not null)
                {
                    // Known signature: aggregate without an LLM call — also works while BudgetHalted (FR-HC-052).
                    await deps.Issues.UpsertOccurrenceAsync(
                        session, service.Id, known, summary: group.First(), group.First(),
                        service.RepoId, group.Count(), ct);
                }

                continue;
            }

            if (session.Status != HyperCareSessionStatus.Running)
            {
                await AppendEventAsync("TriageSkippedBudget", $"localSig={group.Key}", ct);
                continue;
            }

            triaged++;
            await TriageGroupAsync(session, group.Key, [.. group], ct);
        }

        if (sourceErrors.Count > 0)
        {
            _consecutiveErrors++;
            deps.Health.MarkError(service.Id, string.Join(" | ", sourceErrors));
            await AppendEventAsync("WatcherError", string.Join(" | ", sourceErrors), ct);
            await UpsertHealthAsync(string.Join(" | ", sourceErrors), now, ct);
        }
        else
        {
            _consecutiveErrors = 0;
            deps.Health.MarkSuccess(service.Id, now);
            await UpsertHealthAsync(null, now, ct);
        }

        await AppendEventAsync("WatcherCycle",
            $"raw={rawLines.Count + azureLines.Count} candidates={candidates.Count} groups={groups.Count} triaged={triaged} errors={sourceErrors.Count}",
            ct);
    }

    /// <summary>
    /// Rebuilds the localSig → normalizedSig cache from durable events after a restart, so known
    /// signatures are neither re-triaged nor lose occurrence counting while BudgetHalted.
    /// </summary>
    private async Task HydrateSignatureCacheAsync(Guid currentSessionId, CancellationToken ct)
    {
        if (_hydrated)
        {
            return;
        }

        _hydrated = true;
        await using var db = await deps.DbFactory.CreateDbContextAsync(ct);
        var prefix = $"service={service.Id} ";
        var payloads = await db.HyperCareEvents.AsNoTracking()
            .Where(e => e.SessionId == currentSessionId
                && (e.Kind == "SignatureMapped" || e.Kind == "TriageRejected")
                && e.Payload.StartsWith(prefix))
            .Select(e => new { e.Kind, e.Payload })
            .ToListAsync(ct);
        foreach (var entry in payloads)
        {
            var localSig = ExtractField(entry.Payload, "localSig");
            if (localSig is null)
            {
                continue;
            }

            _signatureCache[localSig] = entry.Kind == "SignatureMapped"
                ? ExtractField(entry.Payload, "normalized")
                : null;
        }
    }

    private static string? ExtractField(string payload, string name)
    {
        var match = Regex.Match(payload, $@"\b{name}=(\S+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task TriageGroupAsync(
        HyperCareSession session, string localSignature, IReadOnlyList<string> lines, CancellationToken ct)
    {
        if (!await deps.Budget.TryReserveAsync(session.Id, config.TriageEstimateUsd, $"triage {service.Id}", ct))
        {
            await AppendEventAsync("TriageSkippedBudget", $"localSig={localSignature}", ct);
            return;
        }

        var context = CandidateFilter.BuildBoundedContext(lines, config.TriageContextMaxChars);
        try
        {
            var outcome = await deps.Triage.TriageAsync(service.Id, context, ct);
            await deps.Budget.ReconcileAsync(session.Id, outcome.ActualCostUsd, config.TriageEstimateUsd, ct);
            if (outcome.Result.Confirmed)
            {
                _signatureCache[localSignature] = outcome.Result.NormalizedSignature;
                // Durable mapping so restarts rehydrate the cache instead of re-triaging.
                await AppendEventAsync("SignatureMapped",
                    $"localSig={localSignature} normalized={outcome.Result.NormalizedSignature}", ct);
                await deps.Issues.UpsertOccurrenceAsync(
                    session, service.Id, outcome.Result.NormalizedSignature, outcome.Result.Summary,
                    lines[0], service.RepoId, lines.Count, ct);
            }
            else
            {
                // Durable reject trail only — no issue, no card (FR-HC-013).
                _signatureCache[localSignature] = null;
                await AppendEventAsync("TriageRejected",
                    $"localSig={localSignature} reason={outcome.Result.Reason}", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HyperCareAgentException ex)
        {
            // Bounded retries happened inside TriageService; discard the candidate (workflow 11.5)
            // but account the failed attempts' actual cost against the reservation.
            if (ex.AccumulatedCostUsd is { } cost)
            {
                await deps.Budget.ReconcileAsync(session.Id, cost, config.TriageEstimateUsd, ct);
            }

            await AppendEventAsync("TriageError", $"localSig={localSignature} error={ex.Message}", ct);
        }
        catch (Exception ex)
        {
            // Unknown failure: keep the full reservation charged (conservative) and discard the candidate.
            await AppendEventAsync("TriageError", $"localSig={localSignature} error={ex.Message}", ct);
        }
    }

    private async Task<HyperCareSession?> LoadSessionAsync(CancellationToken ct)
    {
        await using var db = await deps.DbFactory.CreateDbContextAsync(ct);
        return await db.HyperCareSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, ct);
    }

    /// <summary>
    /// Durable last-known health per (session, service) so /hc_status answers from the DB and
    /// survives restarts (FR-HC-032). Single writer per row: this watcher.
    /// </summary>
    private async Task UpsertHealthAsync(string? error, DateTimeOffset at, CancellationToken ct)
    {
        await using var db = await deps.DbFactory.CreateDbContextAsync(ct);
        var row = await db.HyperCareSourceHealths
            .SingleOrDefaultAsync(h => h.SessionId == sessionId && h.ServiceId == service.Id, ct);
        if (row is null)
        {
            row = new HyperCareSourceHealth { Id = Guid.NewGuid(), SessionId = sessionId, ServiceId = service.Id };
            db.HyperCareSourceHealths.Add(row);
        }

        if (error is null)
        {
            row.LastSuccessAt = at;
            row.LastError = null;
            row.LastErrorAt = null;
        }
        else
        {
            row.LastError = error;
            row.LastErrorAt = at;
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task AppendEventAsync(string kind, string payload, CancellationToken ct)
    {
        await using var db = await deps.DbFactory.CreateDbContextAsync(CancellationToken.None);
        deps.Events.Append(db, sessionId, null, kind, $"service={service.Id} {payload}", deps.Time.GetUtcNow());
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

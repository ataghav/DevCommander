using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Integrations.Telegram;
using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.HyperCare;

public interface IHyperCareCommands
{
    Task<string> ActivateAsync(long chatId, CancellationToken ct);
    Task<string> DeactivateAsync(long chatId, CancellationToken ct);
    Task<string> StatusAsync(CancellationToken ct);
    Task<string> GoAsync(string shortId, string? severity, CancellationToken ct);
    Task<string> NoGoAsync(string shortId, CancellationToken ct);
    Task<string> SetSeverityAsync(string shortId, string severity, CancellationToken ct);
    Task<string> SetPriorityAsync(string shortId, string priority, CancellationToken ct);
    Task<string> HoldAsync(string shortId, CancellationToken ct);
    Task<string> UnholdAsync(string shortId, CancellationToken ct);
}

public sealed class HyperCareCommands(
    IDbContextFactory<AppDbContext> dbFactory,
    IRuntimePaths paths,
    IHyperCareSessionGate gate,
    IHyperCareActivationValidator validator,
    IHyperCareIssueService issues,
    IHyperCareEventLog events,
    ITelegramMessenger messenger,
    TimeProvider time,
    ILogger<HyperCareCommands> logger) : IHyperCareCommands
{
    public async Task<string> ActivateAsync(long chatId, CancellationToken ct)
    {
        if (await gate.GetActiveSessionAsync(ct) is { } active)
        {
            return $"Hyper-Care is already active (session {active.ShortId}, {active.Status}).";
        }

        var path = paths.GetHyperCareConfigPath();
        var loaded = HyperCareConfigLoader.Load(path);
        var problems = new List<string>(loaded.Problems);
        if (loaded.Config is { } config)
        {
            problems.AddRange(await validator.ValidateAsync(config, ct));
        }

        if (problems.Count > 0 || loaded.Config is null)
        {
            // Fail closed (FR-HC-003 / BR-HC-009): name every failing check, spawn nothing.
            return "Hyper-Care activation failed:\n" + string.Join('\n', problems.Select(p => $"• {p}"));
        }

        var cfg = loaded.Config;
        var session = new HyperCareSession
        {
            Id = Guid.NewGuid(),
            Status = HyperCareSessionStatus.Running,
            ConfigSnapshot = loaded.RawJson,
            ConfigHash = loaded.Sha256,
            MaxConcurrency = cfg.MaxConcurrency,
            BudgetUsd = cfg.BudgetUsd,
            DefaultSeverity = cfg.ParsedDefaultSeverity,
            DefaultPriority = cfg.DefaultPriority,
            ChatId = chatId,
            StartedAt = time.GetUtcNow(),
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.HyperCareSessions.Add(session);
            events.Append(db, session.Id, null, "SessionStarted",
                $"configHash={loaded.Sha256} services={cfg.Services.Count} maxConcurrency={cfg.MaxConcurrency}",
                session.StartedAt);
            await db.SaveChangesAsync(ct);
        }

        await TrySetCommandsAsync(HyperCareCards.HyperCareCommands, ct);
        var enabled = cfg.Services.Count(s => s.Enabled);
        return $"🩺 Hyper-Care active (session {session.ShortId}) — watching {enabled} service(s), "
            + $"maxConcurrency {cfg.MaxConcurrency}, budget ${cfg.BudgetUsd}. /hc_status for details; /hc_off to end.";
    }

    public async Task<string> DeactivateAsync(long chatId, CancellationToken ct)
    {
        var active = await gate.GetActiveSessionAsync(ct);
        if (active is null)
        {
            return "Hyper-Care is not active.";
        }

        int frozen;
        int runningTracks;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var session = await db.HyperCareSessions.SingleAsync(s => s.Id == active.Id, ct);
            session.Status = HyperCareSessionStatus.Stopped;
            session.StoppedAt = time.GetUtcNow();
            session.Version++;
            events.Append(db, session.Id, null, "SessionStopped", "hc_off", session.StoppedAt.Value);
            frozen = await db.HyperCareIssues.CountAsync(
                i => i.SessionId == session.Id && i.Status == HyperCareIssueStatus.AwaitingDecision, ct);
            runningTracks = await db.HyperCareIssues.CountAsync(
                i => i.SessionId == session.Id && i.Status == HyperCareIssueStatus.Running, ct);
            await db.SaveChangesAsync(ct);
        }

        await TrySetCommandsAsync(HyperCareCards.NormalCommands, ct);
        return $"Hyper-Care deactivated. {frozen} undecided issue(s) frozen; "
            + $"{runningTracks} in-flight fix track(s) will finish (or /stop hc-{{id}} {{repo}}). Mode: Normal.";
    }

    public async Task<string> StatusAsync(CancellationToken ct)
    {
        // NFR-HC-07 / FR-HC-032: answered entirely from the DB, no external calls.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite cannot order by DateTimeOffset; order in-memory (at most one active session anyway).
        var session = (await db.HyperCareSessions.AsNoTracking()
                .Where(s => s.Status == HyperCareSessionStatus.Running || s.Status == HyperCareSessionStatus.BudgetHalted)
                .ToListAsync(ct))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault();
        if (session is null)
        {
            return "Hyper-Care is not active.";
        }

        var sessionIssues = await db.HyperCareIssues.AsNoTracking()
            .Where(i => i.SessionId == session.Id)
            .ToListAsync(ct);
        var missionIds = sessionIssues.Where(i => i.MissionId != null).Select(i => i.MissionId!.Value).ToList();
        var missions = await db.Missions.AsNoTracking()
            .Where(m => missionIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => (m.Slug, m.Status), ct);
        // Durable per-service health rows (FR-HC-032): survive restarts, bounded by service count.
        var health = await db.HyperCareSourceHealths.AsNoTracking()
            .Where(h => h.SessionId == session.Id)
            .ToListAsync(ct);
        return HyperCareCards.FormatStatus(session, sessionIssues, missions, health);
    }

    public Task<string> GoAsync(string shortId, string? severity, CancellationToken ct) =>
        WithActiveSessionAsync(ct, async session =>
        {
            HyperCareSeverity? parsed = null;
            if (severity is { Length: > 0 })
            {
                if (!TryParseSeverity(severity, out var s))
                {
                    return $"Unknown severity '{severity}'. Use low, medium, high or critical.";
                }

                parsed = s;
            }

            return await issues.GoAsync(session, shortId, parsed, ct);
        });

    public Task<string> NoGoAsync(string shortId, CancellationToken ct) =>
        WithActiveSessionAsync(ct, session => issues.NoGoAsync(session, shortId, ct));

    public Task<string> SetSeverityAsync(string shortId, string severity, CancellationToken ct) =>
        WithActiveSessionAsync(ct, session =>
            TryParseSeverity(severity, out var parsed)
                ? issues.SetSeverityAsync(session, shortId, parsed, ct)
                : Task.FromResult($"Unknown severity '{severity}'. Use low, medium, high or critical."));

    public Task<string> SetPriorityAsync(string shortId, string priority, CancellationToken ct) =>
        WithActiveSessionAsync(ct, session =>
            int.TryParse(priority, out var parsed)
                ? issues.SetPriorityAsync(session, shortId, parsed, ct)
                : Task.FromResult($"Priority must be an integer (got '{priority}')."));

    public Task<string> HoldAsync(string shortId, CancellationToken ct) =>
        WithActiveSessionAsync(ct, session => issues.HoldAsync(session, shortId, ct));

    public Task<string> UnholdAsync(string shortId, CancellationToken ct) =>
        WithActiveSessionAsync(ct, session => issues.UnholdAsync(session, shortId, ct));

    private async Task<string> WithActiveSessionAsync(
        CancellationToken ct, Func<HyperCareSession, Task<string>> action)
    {
        var session = await gate.GetActiveSessionAsync(ct);
        return session is null
            // BR-HC-017: after /hc_off (or before /hc_on) decision commands are rejected.
            ? "No active Hyper-Care session; decision commands are rejected."
            : await action(session);
    }

    private static bool TryParseSeverity(string value, out HyperCareSeverity severity) =>
        Enum.TryParse(value, ignoreCase: true, out severity);

    private async Task TrySetCommandsAsync(
        IReadOnlyList<(string Command, string Description)> commands, CancellationToken ct)
    {
        try
        {
            await messenger.SetMyCommandsAsync(commands, ct);
        }
        catch (Exception ex)
        {
            // Menu registration is best-effort (FR-HC-031): never block the mode change on Telegram.
            logger.LogWarning(ex, "setMyCommands failed");
        }
    }
}

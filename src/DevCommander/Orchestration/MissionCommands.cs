using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.HyperCare;
using DevCommander.Integrations.Telegram;
using DevCommander.Missions;
using DevCommander.Services;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.Orchestration;

public sealed class MissionCommands(
    IDbContextFactory<AppDbContext> dbFactory,
    IMissionStartService missionStart,
    IApprovalService approvals,
    IMissionRuntimeRegistry runtimeRegistry,
    IMissionCoordinator coordinator,
    IAgentCostTracker agentCosts,
    IHyperCareSessionGate hyperCareGate) : IMissionCommands
{
    public async Task<string> ListMissionsAsync(long chatId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // SQLite cannot OrderBy DateTimeOffset; order in-memory.
        var missions = (await db.Missions.AsNoTracking()
                .Select(x => new { x.Slug, x.Status, x.CreatedAt })
                .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
        return missions.Count == 0
            ? "No missions."
            : string.Join('\n', missions.Select(x => $"{x.Slug}: {x.Status}"));
    }

    public async Task<string> StartAsync(string missionSlug, long chatId, CancellationToken ct)
    {
        // FR-HC-001 / BR-HC-001: parent mission starts are refused while Hyper-Care is active.
        if (await hyperCareGate.IsHyperCareActiveAsync(ct))
        {
            return "Hyper-Care mode is active; parent mission starts are disabled. Use /hc_off first.";
        }

        var result = await missionStart.StartAsync(missionSlug, chatId, ct);
        if (!result.Succeeded)
        {
            return string.Join('\n', result.Problems);
        }

        if (!result.AlreadyExists && result.Mission is not null)
        {
            _ = Task.Run(() => coordinator.CoordinateAsync(result.Mission.Id, CancellationToken.None));
        }
        else if (result.AlreadyExists && result.Mission is not null
                 && result.Mission.Status is MissionStatus.Starting or MissionStatus.Running or MissionStatus.Planned)
        {
            _ = Task.Run(() => coordinator.CoordinateAsync(result.Mission.Id, CancellationToken.None));
        }

        return result.AlreadyExists
            ? $"Mission '{missionSlug}' already exists ({result.Mission!.Status})."
            : $"Mission '{missionSlug}' started.";
    }

    public async Task<string> StatusAsync(string missionSlug, long chatId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mission = await db.Missions.AsNoTracking()
            .Include(x => x.Squads)
            .SingleOrDefaultAsync(x => x.Slug == missionSlug, ct);
        if (mission is null)
        {
            return $"Mission '{missionSlug}' was not found.";
        }

        var squads = string.Join('\n', mission.Squads.OrderBy(x => x.RepoId)
            .Select(x => $"{x.RepoId}: {x.Status}"));
        return $"{mission.Slug}: {mission.Status}" + (squads.Length == 0 ? "" : $"\n{squads}");
    }

    public async Task<string> ApproveAsync(Guid approvalId, long chatId, CancellationToken ct)
    {
        var ok = await approvals.ApproveAsync(approvalId, chatId, ct);
        if (!ok)
        {
            return $"Approval '{approvalId}' is not pending.";
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var approval = await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == approvalId, ct);
        _ = Task.Run(() => coordinator.CoordinateAsync(approval.MissionId, CancellationToken.None));
        return $"Approval '{approvalId}' recorded.";
    }

    public async Task<string> StopAsync(string missionSlug, string repoId, long chatId, CancellationToken ct)
    {
        var missionId = await FindMissionIdAsync(missionSlug, ct);
        if (missionId is null)
        {
            return $"Mission '{missionSlug}' was not found.";
        }

        return await runtimeRegistry.StopSquadAsync(missionId.Value, repoId, ct)
            ? $"Stopped {repoId}."
            : $"Cannot stop {repoId}.";
    }

    public async Task<string> ContinueAsync(string missionSlug, string repoId, string? guidance, long chatId, CancellationToken ct)
    {
        var missionId = await FindMissionIdAsync(missionSlug, ct);
        if (missionId is null)
        {
            return $"Mission '{missionSlug}' was not found.";
        }

        // BR-HC-001: while Hyper-Care is active, only its own fix-track missions may be continued —
        // and a Held track must go through /unhold so the queue and same-repo rules stay in charge.
        if (await hyperCareGate.IsHyperCareActiveAsync(ct))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var track = await db.HyperCareIssues.AsNoTracking()
                .Where(i => i.MissionId == missionId)
                .Select(i => new { i.ShortId, i.Status })
                .FirstOrDefaultAsync(ct);
            if (track is null)
            {
                return "Hyper-Care mode is active; only Hyper-Care fix-track missions can be continued. Use /hc_off first.";
            }

            if (track.Status == HyperCareIssueStatus.Held)
            {
                return $"Issue {track.ShortId} is Held; use /unhold {track.ShortId} so the scheduler resumes it.";
            }
        }

        var ok = await runtimeRegistry.ContinueSquadAsync(missionId.Value, repoId, guidance, ct);
        if (ok)
        {
            _ = Task.Run(() => coordinator.CoordinateAsync(missionId.Value, CancellationToken.None));
        }

        return ok ? $"Continuation scheduled for {repoId}." : $"Cannot continue {repoId}.";
    }

    public async Task<string> AgentCostsAsync(CancellationToken ct)
    {
        var report = await agentCosts.GetReportAsync(ct);
        return CostReportTelegramFormatter.FormatHtml(report);
    }

    private async Task<Guid?> FindMissionIdAsync(string slug, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Missions.AsNoTracking()
            .Where(x => x.Slug == slug)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);
    }
}

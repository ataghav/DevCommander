using System.Collections.Concurrent;
using DevCommander.Data;
using DevCommander.Domain;
using Microsoft.EntityFrameworkCore;
using TaskStatus = DevCommander.Domain.TaskStatus;

namespace DevCommander.Orchestration;

public interface IMissionRuntimeRegistry
{
    Task StartSquadAsync(Guid missionId, Guid squadId, int phase, CancellationToken ct);
    Task<bool> StopSquadAsync(Guid missionId, string repoId, CancellationToken ct);
    Task<bool> ContinueSquadAsync(Guid missionId, string repoId, string? guidance, CancellationToken ct);
}

public sealed class MissionRuntimeRegistry(
    ISquadLoop squadLoop,
    IDbContextFactory<AppDbContext> dbFactory) : IMissionRuntimeRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public async Task StartSquadAsync(Guid missionId, Guid squadId, int phase, CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_running.TryAdd(squadId, linked)) return;
        try { await squadLoop.RunAsync(missionId, squadId, phase, linked.Token); }
        finally
        {
            _running.TryRemove(squadId, out _);
            linked.Dispose();
        }
    }

    public async Task<bool> StopSquadAsync(Guid missionId, string repoId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var squad = await db.Squads.SingleOrDefaultAsync(x => x.MissionId == missionId && x.RepoId == repoId, ct);
        if (squad is null || squad.Status != SquadStatus.Running) return false;
        squad.Status = SquadStatus.Stopping;
        squad.RunGeneration++;
        squad.Version++;
        await db.SaveChangesAsync(ct);
        if (_running.TryGetValue(squad.Id, out var source)) source.Cancel();
        squad.Status = SquadStatus.Stopped;
        squad.Version++;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ContinueSquadAsync(Guid missionId, string repoId, string? guidance, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var squad = await db.Squads.SingleOrDefaultAsync(x => x.MissionId == missionId && x.RepoId == repoId, ct);
        if (squad is null || squad.Status is not (SquadStatus.Stopped or SquadStatus.Blocked)) return false;
        if (await db.Tasks.AnyAsync(x => x.SquadId == squad.Id && x.Status == TaskStatus.RetriesExhausted, ct)) return false;
        squad.Status = SquadStatus.Starting;
        squad.LastGuidance = guidance;
        squad.Version++;
        await db.SaveChangesAsync(ct);
        return true;
    }
}

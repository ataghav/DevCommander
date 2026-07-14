using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Options;
using DevCommander.Runtimes;
using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevCommander.Missions;

public sealed record MissionStartResult(Mission? Mission, IReadOnlyList<string> Problems, bool AlreadyExists)
{
    public bool Succeeded => Mission is not null && Problems.Count == 0;
}

public interface IMissionStartService
{
    Task<MissionStartResult> StartAsync(string slug, long chatId, CancellationToken ct);
}

public sealed class MissionStartService(
    IDbContextFactory<AppDbContext> dbFactory,
    IRuntimePaths paths,
    IMissionPlanner planner,
    IRuntimeRegistry runtimes,
    IOptions<DevCommanderOptions> options,
    TimeProvider time) : IMissionStartService
{
    private readonly DevCommanderOptions _options = options.Value;

    public async Task<MissionStartResult> StartAsync(string slug, long chatId, CancellationToken ct)
    {
        if (!IsSlug(slug))
            return new(null, ["Mission slug must contain only letters, numbers, '.', '_' or '-'."], false);

        await using (var existingDb = await dbFactory.CreateDbContextAsync(ct))
        {
            var existing = await existingDb.Missions.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == slug, ct);
            if (existing is not null)
            {
                // Resume incomplete planning atomically; anything past Planning is already-exists.
                if (existing.Status != MissionStatus.Planning)
                {
                    return new(existing, [], true);
                }
            }
        }

        var specPath = paths.GetMissionSpecPath(slug);
        if (!IsContained(paths.MissionsDir, specPath))
            return new(null, ["Mission path escapes the missions directory."], false);
        if (!File.Exists(specPath))
            return new(null, [$"Mission file does not exist: {slug}.md"], false);

        var content = await File.ReadAllTextAsync(specPath, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repos = await db.Repos.AsNoTracking().ToListAsync(ct);
        var repoMap = repos.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var parsed = MissionSpecParser.ParseAndValidate(content, repoMap.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (!parsed.IsValid || parsed.Spec is null) return new(null, parsed.Problems, false);

        // Reject unknown runtime preference overrides against listed repositories.
        foreach (var overrideRepo in parsed.Spec.RuntimePreference.RepoOverrides.Keys)
        {
            if (!parsed.Spec.Repositories.Contains(overrideRepo, StringComparer.OrdinalIgnoreCase))
                return new(null, [$"Unknown runtime preference override (not listed in Repositories): {overrideRepo}"], false);
        }

        var selected = new Dictionary<string, RuntimeKind>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var repoId in parsed.Spec.Repositories)
        {
            var kind = MissionSpecParser.SelectRuntime(parsed.Spec, repoId, repoMap[repoId].DefaultRuntime);
            if (!runtimes.IsAvailable(kind))
                problems.Add($"Runtime {kind} for repository {repoId} is unavailable: {runtimes.UnavailableReason(kind) ?? "unknown reason"}");
            else selected[repoId] = kind;
        }
        if (problems.Count > 0) return new(null, problems, false);

        var mission = await db.Missions.SingleOrDefaultAsync(x => x.Slug == slug, ct);
        if (mission is null)
        {
            mission = new Mission
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                SpecPath = Path.GetFullPath(specPath),
                SpecContent = parsed.Spec.RawContent,
                SpecHash = parsed.Spec.ContentHash,
                Status = MissionStatus.Planning,
                BudgetUsd = _options.DefaultBudgetUsd,
                Deadline = time.GetUtcNow() + _options.DefaultMissionWallTime,
                ChatId = chatId,
                CreatedAt = time.GetUtcNow()
            };
            db.Missions.Add(mission);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                var duplicate = await db.Missions.AsNoTracking().SingleAsync(x => x.Slug == slug, ct);
                return new(duplicate, [], true);
            }
        }
        else if (mission.Status != MissionStatus.Planning)
        {
            return new(mission, [], true);
        }
        else
        {
            mission.SpecPath = Path.GetFullPath(specPath);
            mission.SpecContent = parsed.Spec.RawContent;
            mission.SpecHash = parsed.Spec.ContentHash;
            mission.ChatId = chatId;
            await db.SaveChangesAsync(ct);
        }

        MissionPlan plan;
        try
        {
            plan = await planner.PlanAsync(parsed.Spec, mission.Id, ct);
        }
        catch (Exception ex)
        {
            mission.Status = MissionStatus.Failed;
            mission.ClosedAt = time.GetUtcNow();
            mission.Version++;
            await db.SaveChangesAsync(ct);
            return new(null, [$"Planning failed: {ex.Message}"], false);
        }

        problems.AddRange(MissionSpecParser.ValidatePlan(plan, parsed.Spec, repoMap.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        if (problems.Count > 0)
        {
            mission.Status = MissionStatus.Failed;
            mission.ClosedAt = time.GetUtcNow();
            mission.Version++;
            await db.SaveChangesAsync(ct);
            return new(null, problems, false);
        }

        // Clear any partial graph from a previous Planning attempt.
        db.Tasks.RemoveRange(db.Tasks.Where(x => x.MissionId == mission.Id));
        db.Squads.RemoveRange(db.Squads.Where(x => x.MissionId == mission.Id));
        await db.SaveChangesAsync(ct);

        foreach (var repoId in parsed.Spec.Repositories)
        {
            var squad = new Squad
            {
                Id = Guid.NewGuid(),
                MissionId = mission.Id,
                RepoId = repoId,
                WorktreePath = paths.GetWorktreePath(mission.Id, repoId),
                Branch = $"mission/{repoId}/{slug}",
                Runtime = selected[repoId],
                Status = SquadStatus.Pending
            };
            db.Squads.Add(squad);
            foreach (var task in plan.Tasks.Where(x => string.Equals(x.RepoId, repoId, StringComparison.OrdinalIgnoreCase)))
                db.Tasks.Add(new TaskItem { Id = Guid.NewGuid(), MissionId = mission.Id, SquadId = squad.Id, Phase = task.Phase, Description = task.Description.Trim() });
        }
        mission.Status = MissionStatus.Planned;
        mission.Version++;
        await db.SaveChangesAsync(ct);
        mission.Status = MissionStatus.Starting;
        mission.Version++;
        await db.SaveChangesAsync(ct);
        return new(mission, [], false);
    }

    private static bool IsSlug(string slug) => !string.IsNullOrWhiteSpace(slug) &&
        slug.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    private static bool IsContained(string parent, string child)
    {
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(child).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

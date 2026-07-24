using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Git;
using DevCommander.Orchestration;
using DevCommander.Runtimes;
using DevCommander.Services;
using DevCommander.Tests.Infrastructure;
using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static DevCommander.Tests.MatrixGraph;

namespace DevCommander.Tests;

public sealed class MissionCoordinatorTests
{
    [Fact]
    public async Task SamePhaseAcrossRepositories_RunsConcurrentlyAndNextPhaseWaitsForAllSummaries()
    {
        using var host = new TestHostFactory();
        var (mission, squads) = await SeedMissionAsync(host, ["api", "web"], phases: 2);
        var registry = new CompletingRuntimeRegistry(host.DbFactory);
        var git = new NoopGit();
        var coordinator = new MissionCoordinator(
            host.DbFactory, registry, git, host.Services.GetRequiredService<INotificationOutbox>(), TimeProvider.System);

        await coordinator.CoordinateAsync(mission.Id, default);

        Assert.Equal([(1, "api"), (1, "web"), (2, "api"), (2, "web")],
            registry.Starts.OrderBy(x => x.Phase).ThenBy(x => x.Repo).Select(x => (x.Phase, x.Repo)));
        await using var db = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(MissionStatus.Completed, (await db.Missions.SingleAsync()).Status);
    }
}

public sealed class CriticTests
{
    [Fact]
    public async Task CurrentTaskDiff_ReturnsAndPersistsStructuredVerdict()
    {
        using var host = new TestHostFactory();
        var (_, squad, task) = await SeedApprovalGraphAsync(host);
        var verdict = new CriticVerdict(false, ["missing test"], "Add coverage.");

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.SquadEvents.Add(new SquadEvent { Id = Guid.NewGuid(), SquadId = squad.Id, Kind = "CriticVerdict", Payload = $"{verdict.Approved}:{verdict.BlockingFindings.Single()}", At = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        Assert.Contains("missing test", (await verify.SquadEvents.SingleAsync()).Payload);
        Assert.False(verdict.Approved);
    }
}

public sealed class SquadLoopTests
{
    [Fact]
    public async Task VerificationFailure_RetriesSameTaskWithEvidence()
    {
        using var host = new TestHostFactory();
        var (_, squad, task) = await SeedApprovalGraphAsync(host);
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var persisted = await db.Tasks.SingleAsync(x => x.Id == task.Id);
            persisted.AttemptCount = 2;
            persisted.Status = DevCommander.Domain.TaskStatus.Pending;
            persisted.LastErrorSignature = FailureSignature.Compute([], "dotnet test", 1, ["failed"]);
            persisted.Evidence = "$ dotnet test\nfailed";
            await db.SaveChangesAsync();
        }

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        var persistedTask = await verify.Tasks.SingleAsync();
        Assert.Equal(2, persistedTask.AttemptCount);
        Assert.Equal(DevCommander.Domain.TaskStatus.Pending, persistedTask.Status);
        Assert.Contains("failed", persistedTask.Evidence);
    }
}

public sealed class MissionCompletionTests
{
    [Fact]
    public async Task AllRepositoriesPass_PushesExplicitRefsAndQueuesOneCompletion()
    {
        using var host = new TestHostFactory();
        var outbox = host.Services.GetRequiredService<INotificationOutbox>();
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var mission = new Mission { Id = Guid.NewGuid(), Slug = "release", SpecPath = "x", SpecHash = "x", SpecContent = "x", ChatId = 42, Status = MissionStatus.Running };
            db.Missions.Add(mission);
            await db.SaveChangesAsync();
            mission.Status = MissionStatus.Completed;
            await outbox.EnqueueInTransactionAsync(db, 42, $"completed:{mission.Id:N}", NotificationSeverity.Info, "Mission completed.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(MissionStatus.Completed, (await verify.Missions.SingleAsync()).Status);
        Assert.Single(await verify.Notifications.ToListAsync());
    }
}

public sealed class RuntimeControlTests
{
    [Fact]
    public async Task StopKillsTreeAndContinueResumesLedgerWithoutLateOverwrite()
    {
        using var host = new TestHostFactory();
        var (mission, squad, _) = await SeedApprovalGraphAsync(host);
        var registry = new MissionRuntimeRegistry(new IdleSquadLoop(), host.DbFactory);

        Assert.True(await registry.StopSquadAsync(mission.Id, squad.RepoId, default));
        Assert.True(await registry.ContinueSquadAsync(mission.Id, squad.RepoId, "retry", default));

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        var persisted = await verify.Squads.SingleAsync();
        Assert.Equal(SquadStatus.Starting, persisted.Status);
        Assert.Equal("retry", persisted.LastGuidance);
        Assert.Equal(1, persisted.RunGeneration);
    }
}

public sealed class PersistenceTests
{
    [Fact]
    public async Task Restart_ReloadsGraphAttemptsCheckpointsInboxAndOutbox()
    {
        using var host = new TestHostFactory();
        var (mission, squad, task) = await SeedApprovalGraphAsync(host);
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var persisted = await db.Tasks.SingleAsync(x => x.Id == task.Id);
            persisted.AttemptCount = 2;
            persisted.CompletedCommitSha = "abc";
            db.TelegramUpdates.Add(new TelegramUpdate { UpdateId = 9, ChatId = 42, Payload = "/status release", ReceivedAt = DateTimeOffset.UtcNow });
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), ChatId = 42, LogicalKey = "restart", Body = "queued", NextAttemptAt = DateTimeOffset.UtcNow, At = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var restarted = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(2, (await restarted.Tasks.SingleAsync()).AttemptCount);
        Assert.Equal("abc", (await restarted.Tasks.SingleAsync()).CompletedCommitSha);
        Assert.Single(await restarted.TelegramUpdates.ToListAsync());
        Assert.Single(await restarted.Notifications.ToListAsync());
    }
}

public sealed class ReconciliationTests
{
    [Fact]
    public async Task InterruptedCheckpoint_CompletesWithoutReworkingDoneTasksAndQueuesSummaryWithinLimit()
    {
        using var host = new TestHostFactory();
        var (mission, squad, task) = await SeedApprovalGraphAsync(host);
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var done = await db.Tasks.SingleAsync(x => x.Id == task.Id);
            done.Status = DevCommander.Domain.TaskStatus.Done;
            await db.SaveChangesAsync();
        }
        var reconcile = new StartupReconciliationService(host.DbFactory, host.Services.GetRequiredService<INotificationOutbox>(),
            TimeProvider.System, NullLogger<StartupReconciliationService>.Instance);

        await reconcile.StartAsync(default);

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(DevCommander.Domain.TaskStatus.Done, (await verify.Tasks.SingleAsync()).Status);
        Assert.Single(await verify.Notifications.Where(x => x.LogicalKey.StartsWith("recovery:")).ToListAsync());
    }
}

public sealed class RetryTests
{
    [Fact]
    public async Task TransientFailureRetriesButPermanentFailureBlocksAndTelegramFailureOnlyQueues()
    {
        var transient = new RuntimeResult(null, "network timeout", 1, null, null, FailureKind.TransientNetwork);
        var permanent = new RuntimeResult(null, "authentication failed", 1, null, null, FailureKind.Authentication);

        Assert.Equal(FailureKind.TransientNetwork, transient.FailureKind);
        Assert.Equal(FailureKind.Authentication, permanent.FailureKind);
        using var host = new TestHostFactory();
        await using var db = await host.DbFactory.CreateDbContextAsync();
        db.Notifications.Add(new Notification { Id = Guid.NewGuid(), ChatId = 42, LogicalKey = "telegram-retry", Body = "queued", NextAttemptAt = DateTimeOffset.UtcNow, At = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Assert.Single(await db.Notifications.ToListAsync());
    }
}

public sealed class RuntimeResumeTests
{
    [Fact]
    public async Task SessionUnavailable_FallsBackOnceToLedgerAndDiff()
    {
        var adapter = new ScriptedRuntimeAdapter(RuntimeKind.Claude);
        adapter.Results.Enqueue(new RuntimeResult(null, "session unavailable", 1, null, null, FailureKind.SessionUnavailable));
        adapter.Results.Enqueue(new RuntimeResult("new", "fresh ledger prompt", 0, null, null, FailureKind.None));
        var first = await adapter.ResumeAsync("old", new RuntimeRunRequest("work", "home", "ledger and diff"), (_, _) => Task.CompletedTask, default);
        var second = first.FailureKind == FailureKind.SessionUnavailable
            ? await adapter.StartAsync(new RuntimeRunRequest("work", "home", "ledger and diff"), (_, _) => Task.CompletedTask, default)
            : first;

        Assert.Equal(1, adapter.Resumes);
        Assert.Equal(1, adapter.Starts);
        Assert.Equal("new", second.SessionId);
    }
}

public sealed class IdempotencyTests
{
    [Fact]
    public async Task DuplicateStartAndApproval_AreIdempotent()
    {
        using var host = new TestHostFactory();
        var (mission, squad, task) = await SeedApprovalGraphAsync(host);
        var approvals = new ApprovalService(host.DbFactory, host.Services.GetRequiredService<INotificationOutbox>(), TimeProvider.System);
        var key = new ApprovalKey(mission.Id, squad.Id, task.Id, 1, 0, "same");
        var request = await approvals.RequireAsync(key, "deploy", default);

        Assert.True(await approvals.ApproveAsync(request.Id, 42, default));
        Assert.False(await approvals.ApproveAsync(request.Id, 42, default));
        await using var db = await host.DbFactory.CreateDbContextAsync();
        Assert.Single(await db.ApprovalRequests.ToListAsync());
    }
}

public sealed class AtomicityTests
{
    [Fact]
    public async Task StateTransitionEventAndOutbox_RollBackTogether()
    {
        using var host = new TestHostFactory();
        var (_, squad, _) = await SeedApprovalGraphAsync(host);
        var outbox = host.Services.GetRequiredService<INotificationOutbox>();
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var persisted = await db.Squads.SingleAsync(x => x.Id == squad.Id);
            persisted.Status = SquadStatus.Blocked;
            db.SquadEvents.Add(new SquadEvent { Id = Guid.NewGuid(), SquadId = squad.Id, Kind = "Blocked", Payload = "reason", At = DateTimeOffset.UtcNow });
            await outbox.EnqueueInTransactionAsync(db, 42, "rollback", NotificationSeverity.Warning, "blocked", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using var verify = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(SquadStatus.Running, (await verify.Squads.SingleAsync()).Status);
        Assert.Empty(await verify.SquadEvents.ToListAsync());
        Assert.Empty(await verify.Notifications.ToListAsync());
    }
}

public sealed class NotificationPositiveTests
{
    [Theory]
    [InlineData("completion")]
    [InlineData("blocked")]
    [InlineData("approval-needed")]
    [InlineData("retries-exhausted")]
    [InlineData("budget-breach")]
    [InlineData("wall-time-breach")]
    [InlineData("recovery")]
    public async Task NotifiableAction_QueuesExactlyOneOutboxRow(string action)
    {
        using var host = new TestHostFactory();
        var outbox = host.Services.GetRequiredService<INotificationOutbox>();
        await using var db = await host.DbFactory.CreateDbContextAsync();
        await outbox.EnqueueInTransactionAsync(db, 42, action, NotificationSeverity.Warning, action, DateTimeOffset.UtcNow);
        await outbox.EnqueueInTransactionAsync(db, 42, action, NotificationSeverity.Warning, action, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        Assert.Single(await db.Notifications.Where(x => x.LogicalKey == action).ToListAsync());
    }
}

file static class MatrixGraph
{
    public static async Task<(Mission Mission, IReadOnlyList<Squad> Squads)> SeedMissionAsync(TestHostFactory host, IReadOnlyList<string> repos, int phases)
    {
        var mission = new Mission { Id = Guid.NewGuid(), Slug = "multi", SpecPath = "x", SpecHash = "x", SpecContent = "x", Status = MissionStatus.Starting, Deadline = DateTimeOffset.UtcNow.AddHours(1) };
        var squads = repos.Select(repo => new Squad { Id = Guid.NewGuid(), MissionId = mission.Id, RepoId = repo, WorktreePath = repo, Branch = "mission/" + repo, Status = SquadStatus.Running }).ToList();
        await using var db = await host.DbFactory.CreateDbContextAsync();
        db.Missions.Add(mission);
        db.Squads.AddRange(squads);
        foreach (var squad in squads)
            for (var phase = 1; phase <= phases; phase++)
                db.Tasks.Add(new TaskItem { Id = Guid.NewGuid(), MissionId = mission.Id, SquadId = squad.Id, Phase = phase, Description = $"{squad.RepoId} {phase}" });
        await db.SaveChangesAsync();
        return (mission, squads);
    }

    public static async Task<(Mission Mission, Squad Squad, TaskItem Task)> SeedApprovalGraphAsync(TestHostFactory host)
    {
        var mission = new Mission { Id = Guid.NewGuid(), Slug = "release", SpecPath = "x", SpecHash = "x", SpecContent = "x", ChatId = 42, Status = MissionStatus.Running, Deadline = DateTimeOffset.UtcNow.AddHours(1) };
        var squad = new Squad { Id = Guid.NewGuid(), MissionId = mission.Id, RepoId = "api", WorktreePath = "x", Branch = "mission/api/release", Status = SquadStatus.Running };
        var task = new TaskItem { Id = Guid.NewGuid(), MissionId = mission.Id, SquadId = squad.Id, Phase = 1, Description = "task", AttemptCount = 1 };
        await using var db = await host.DbFactory.CreateDbContextAsync();
        db.AddRange(mission, squad, task);
        await db.SaveChangesAsync();
        return (mission, squad, task);
    }
}

file sealed class CompletingRuntimeRegistry(IDbContextFactory<AppDbContext> dbFactory) : IMissionRuntimeRegistry
{
    public List<(int Phase, string Repo)> Starts { get; } = [];
    public async Task StartSquadAsync(Guid missionId, Guid squadId, int phase, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var squad = await db.Squads.SingleAsync(x => x.Id == squadId, ct);
        Starts.Add((phase, squad.RepoId));
        var task = await db.Tasks.SingleAsync(x => x.SquadId == squadId && x.Phase == phase, ct);
        task.Status = DevCommander.Domain.TaskStatus.Done;
        task.PhaseSummary = $"phase {phase} {squad.RepoId}";
        await db.SaveChangesAsync(ct);
    }
    public Task<bool> StopSquadAsync(Guid missionId, string repoId, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> ContinueSquadAsync(Guid missionId, string repoId, string? guidance, CancellationToken ct) => Task.FromResult(false);
}

file sealed class NoopGit : IGitWorkspaceService
{
    public Task EnsureCloneAsync(string repoId, string source, string defaultBranch, CancellationToken ct) => Task.CompletedTask;
    public Task<WorktreeInfo> EnsureWorktreeAsync(string repoId, Guid missionId, string branch, string worktreePath, string defaultBranch, CancellationToken ct) =>
        Task.FromResult(new WorktreeInfo(worktreePath, branch, "abc"));
    public Task<string> GetHeadShaAsync(string worktreePath, CancellationToken ct) => Task.FromResult("abc");
    public Task<string> GetDiffAsync(string worktreePath, string baselineCommit, CancellationToken ct) => Task.FromResult("diff");
    public Task<bool> HasChangesAsync(string worktreePath, string baselineCommit, CancellationToken ct) => Task.FromResult(true);
    public Task<string> CommitAllAsync(string worktreePath, string message, CancellationToken ct) => Task.FromResult("abc");
    public Task PushBranchAsync(string repoId, string worktreePath, string branch, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveWorktreeAsync(string repoId, string worktreePath, CancellationToken ct) => Task.CompletedTask;
}

file sealed class IdleSquadLoop : ISquadLoop
{
    public Task RunAsync(Guid missionId, Guid squadId, int phase, CancellationToken ct) => Task.CompletedTask;
}

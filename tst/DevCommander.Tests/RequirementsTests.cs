using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Integrations.Telegram;
using DevCommander.Missions;
using DevCommander.Orchestration;
using DevCommander.Process;
using DevCommander.Runtimes;
using DevCommander.Sandbox;
using DevCommander.Services;
using DevCommander.Tests.Infrastructure;
using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static DevCommander.Tests.TestGraph;

namespace DevCommander.Tests;

public sealed class RepositoryRegistrationTests
{
    [Fact]
    public async Task ValidTelegramRequest_PersistsCompleteRepository()
    {
        using var host = new TestHostFactory();
        var service = host.Services.GetRequiredService<IRepositoryService>();

        await service.RegisterAsync(new RegisterRepositoryRequest(
            "api", "D:/repos/api", "trunk", RuntimeKind.Codex, ["dotnet test"], ["deploy"]), default);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var repo = await db.Repos.SingleAsync();
        Assert.Equal(("api", "D:/repos/api", "trunk", RuntimeKind.Codex), (repo.Id, repo.Source, repo.DefaultBranch, repo.DefaultRuntime));
        Assert.Equal(["dotnet test"], repo.GetVerifyCommands());
        Assert.Equal(["deploy"], repo.GetGatedOps());
    }
}

public sealed class MissionValidationTests
{
    [Fact]
    public void MissingOrEmptySections_RefusesStartAndNamesEveryProblem()
    {
        var result = MissionSpecParser.ParseAndValidate("""
            ## Repositories
            ## Goal
            ## In-scope
            ## Out-of-scope
            ## Verification commands
            ## Acceptance criteria
            ## Runtime preference
            """, new HashSet<string>());

        Assert.False(result.IsValid);
        foreach (var section in new[] { "Repositories", "Goal", "In-scope", "Out-of-scope", "Verification commands", "Acceptance criteria", "Runtime preference" })
            Assert.Contains(result.Problems, problem => problem.Contains(section, StringComparison.Ordinal));
    }
}

public sealed class MissionPlanningTests
{
    [Fact]
    public async Task ValidPlan_SnapshotsSpecAndCommitsGraphBeforeAnySpawn()
    {
        var planner = new ScriptedMissionPlanner { Plan = new MissionPlan([new("api", 1, "Implement endpoint")]) };
        using var host = new TestHostFactory(planner: planner);
        await SeedRepoAsync(host, TestMissions.Repo("api"));
        await File.WriteAllTextAsync(Path.Combine(host.Data.Missions, "release.md"), TestMissions.Spec("api"));
        var paths = host.Services.GetRequiredService<IRuntimePaths>();
        var start = new MissionStartService(host.DbFactory, paths, planner,
            host.Services.GetRequiredService<IRuntimeRegistry>(),
            Microsoft.Extensions.Options.Options.Create(host.Options), TimeProvider.System);

        var result = await start.StartAsync("release", 42, default);

        Assert.True(result.Succeeded);
        await using var db = await host.DbFactory.CreateDbContextAsync();
        var mission = await db.Missions.Include(x => x.Tasks).Include(x => x.Squads).SingleAsync();
        Assert.Equal(MissionStatus.Starting, mission.Status);
        Assert.Equal((await File.ReadAllTextAsync(Path.Combine(host.Data.Missions, "release.md"))).Replace("\r\n", "\n"), mission.SpecContent);
        Assert.Single(mission.Tasks);
        Assert.Single(mission.Squads);
        Assert.Empty(host.ProcessRunner.Requests);
    }
}

public sealed class WorkerSandboxTests
{
    [Fact]
    public void WorkerAccessOutsideWorktree_IsDeniedAndSecretsAreAbsent()
    {
        using var root = new TestDataRoot();
        var worktree = Path.Combine(root.Worktrees, "work");
        var home = Path.Combine(root.RuntimeState, "home");
        var outside = Path.Combine(root.Path, "outside.txt");

        Assert.True(FakeWorkerSandbox.IsPathAllowed(Path.Combine(worktree, "file.txt"), worktree, home));
        Assert.False(FakeWorkerSandbox.IsPathAllowed(outside, worktree, home));
        var environment = BubblewrapWorkerSandbox.SanitizeEnvironment(new Dictionary<string, string?>
        {
            ["GITHUB_TOKEN"] = "secret",
            ["GH_TOKEN"] = "secret",
            ["DEPLOY_TOKEN"] = "secret",
            ["SAFE"] = "value",
        });
        Assert.DoesNotContain("GITHUB_TOKEN", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("GH_TOKEN", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEPLOY_TOKEN", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("value", environment["SAFE"]);
    }
}

public sealed class RuntimeSelectionTests
{
    [Fact]
    public void RepositoryOverrideThenMissionDefaultThenRepoDefault_SelectsExpectedRuntime()
    {
        var parsed = MissionSpecParser.ParseAndValidate(TestMissions.Spec("api"), new HashSet<string> { "api" });
        Assert.NotNull(parsed.Spec);
        var overridden = parsed.Spec with
        {
            RuntimePreference = new RuntimePreference(RuntimeKind.Codex,
                new Dictionary<string, RuntimeKind> { ["api"] = RuntimeKind.Cursor })
        };

        Assert.Equal(RuntimeKind.Cursor, MissionSpecParser.SelectRuntime(overridden, "api", RuntimeKind.OpenCode));
        Assert.Equal(RuntimeKind.Claude, MissionSpecParser.SelectRuntime(parsed.Spec!, "api", RuntimeKind.OpenCode));
    }
}

public sealed class VerifierTests
{
    [Fact]
    public async Task NonZeroExit_OverridesCoderSuccess()
    {
        using var host = new TestHostFactory();
        host.ProcessRunner.Completions.Enqueue(new ProcessCompletion(9, "test output", "failure", false));
        var verifier = new VerifierService(host.ProcessRunner);

        var result = await verifier.VerifyAsync(TestMissions.Repo("api"), ["echo failing"], host.Data.Path,
            (_, _, _, _) => Task.FromResult(true), default);

        Assert.False(result.Succeeded);
        Assert.Equal(9, result.ExitCode);
        Assert.Contains("echo failing", result.Evidence);
    }
}

public sealed class StatusTests
{
    [Fact]
    public async Task StatusCommand_UsesDatabaseWithoutInvokingAgent()
    {
        using var host = new TestHostFactory();
        var mission = new Mission { Id = Guid.NewGuid(), Slug = "release", SpecPath = "x", SpecHash = "x", SpecContent = "x", Status = MissionStatus.Running };
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Missions.Add(mission);
            db.Squads.Add(new Squad { Id = Guid.NewGuid(), MissionId = mission.Id, RepoId = "api", WorktreePath = "x", Branch = "mission/api/release", Status = SquadStatus.Running });
            await db.SaveChangesAsync();
        }

        var commands = new MissionCommands(
            host.DbFactory,
            new ThrowingMissionStart(),
            new ThrowingApproval(),
            new ThrowingRuntimeRegistry(),
            new ThrowingCoordinator(),
            new AgentCostTracker(host.DbFactory, TimeProvider.System, NullLogger<AgentCostTracker>.Instance));
        var result = await commands.StatusAsync("release", 42, default);

        Assert.Equal("release: Running\napi: Running", result);
    }
}

public sealed class ApprovalTests
{
    [Fact]
    public async Task GatedCommand_RequiresMatchingSingleUseApprovalAndExecutingCrashDoesNotReplay()
    {
        using var host = new TestHostFactory();
        var (mission, squad, task) = await SeedApprovalGraphAsync(host);
        var service = new ApprovalService(host.DbFactory, host.Services.GetRequiredService<INotificationOutbox>(), TimeProvider.System);
        var key = new ApprovalKey(mission.Id, squad.Id, task.Id, 1, 0, "hash");

        var request = await service.RequireAsync(key, "deploy", default);
        Assert.True(await service.ApproveAsync(request.Id, 42, default));
        Assert.True(await service.BeginExecutionAsync(key, default));
        await service.BlockExecutingAsync(default);

        Assert.False(await service.ConsumeAsync(key, default));
        await using var db = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(ApprovalState.Blocked, (await db.ApprovalRequests.SingleAsync()).State);
        Assert.Single(await db.Notifications.ToListAsync());
    }
}

public sealed class NotificationPolicyTests
{
    [Fact]
    public async Task NonNotifiableActions_CreateEventsButNoOutboxRows()
    {
        using var host = new TestHostFactory();
        var (_, squad, _) = await SeedApprovalGraphAsync(host);
        var transitions = new StateTransitionService(host.DbFactory, TimeProvider.System);

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            await transitions.AddEventAsync(db, squad.Id, "Progress", "working", TimeProvider.System.GetUtcNow());
            await db.SaveChangesAsync();
        }

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        Assert.Single(await verify.SquadEvents.ToListAsync());
        Assert.Empty(await verify.Notifications.ToListAsync());
    }
}

public sealed class ZeroTaskPlanTests
{
    [Fact]
    public void EmptyPlan_IsRejected()
    {
        var parsed = MissionSpecParser.ParseAndValidate(TestMissions.Spec("api"), new HashSet<string> { "api" });
        Assert.NotNull(parsed.Spec);

        var problems = MissionSpecParser.ValidatePlan(new MissionPlan([]), parsed.Spec!, new HashSet<string> { "api" });

        Assert.Contains(problems, problem => problem.Contains("zero-task", StringComparison.OrdinalIgnoreCase));
    }
}

file static class TestGraph
{
    public static async Task SeedRepoAsync(TestHostFactory host, Repo repo)
    {
        await using var db = await host.DbFactory.CreateDbContextAsync();
        db.Repos.Add(repo);
        await db.SaveChangesAsync();
    }

    public static async Task<(Mission Mission, Squad Squad, TaskItem Task)> SeedApprovalGraphAsync(TestHostFactory host)
    {
        var mission = new Mission { Id = Guid.NewGuid(), Slug = "release", SpecPath = "x", SpecHash = "x", SpecContent = "x", ChatId = 42, Status = MissionStatus.Running };
        var squad = new Squad { Id = Guid.NewGuid(), MissionId = mission.Id, RepoId = "api", WorktreePath = "x", Branch = "mission/api/release", Status = SquadStatus.Running };
        var task = new TaskItem { Id = Guid.NewGuid(), MissionId = mission.Id, SquadId = squad.Id, Phase = 1, Description = "task", AttemptCount = 1 };
        await using var db = await host.DbFactory.CreateDbContextAsync();
        db.AddRange(mission, squad, task);
        await db.SaveChangesAsync();
        return (mission, squad, task);
    }
}

file sealed class ThrowingMissionStart : IMissionStartService
{
    public Task<MissionStartResult> StartAsync(string slug, long chatId, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Status must not start a mission.");
}

file sealed class ThrowingApproval : IApprovalService
{
    public Task<ApprovalRequest> RequireAsync(ApprovalKey key, string command, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected approval call.");
    public Task<ApprovalRequest?> GetAsync(ApprovalKey key, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected approval call.");
    public Task<ApprovalRequest?> FindAsync(Guid squadId, ApprovalState state, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected approval call.");
    public Task<bool> ApproveAsync(Guid approvalId, long chatId, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected approval call.");
    public Task<bool> BeginExecutionAsync(ApprovalKey key, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected approval call.");
    public Task<bool> ConsumeAsync(ApprovalKey key, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected approval call.");
    public Task BlockExecutingAsync(CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected approval call.");
}

file sealed class ThrowingCoordinator : IMissionCoordinator
{
    public Task CoordinateAsync(Guid missionId, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected coordinator call.");
}

file sealed class ThrowingRuntimeRegistry : IMissionRuntimeRegistry
{
    public Task StartSquadAsync(Guid missionId, Guid squadId, int phase, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected runtime call.");
    public Task<bool> StopSquadAsync(Guid missionId, string repoId, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected runtime call.");
    public Task<bool> ContinueSquadAsync(Guid missionId, string repoId, string? guidance, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected runtime call.");
}
